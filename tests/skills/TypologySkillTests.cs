using Bim.Core;
using System.Globalization;
using System.Text.Json.Nodes;

// P5-07 oracle: the vivienda unifamiliar typology pack (skills/bim.typology.vivienda-unifamiliar) states a room programme
// (required and optional rooms, minimum/target/maximum areas, adjacency wishes) as programme.* defaults; SkillTypology
// resolves it for a family size into a programme record and a project brief v1 that BriefValidator accepts, with typed
// refusals for unknown room kinds, conflicting minimums and invalid settings, deterministically. Unit-tested only.
static class TypologySkillTests
{
    const string Pack = "bim.typology.vivienda-unifamiliar";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Dwelling = "bim.typology.dwelling";
    const string RoomQa = "bim.qa.room-basics";

    // Expected programme per family size: room ids in order with their counts.
    static readonly (int FamilySize, (string Kind, int Count)[] Rooms)[] Programmes =
    {
        (1, new[] { ("living", 1), ("dining", 1), ("kitchen", 1), ("hall", 1), ("bedroom", 1), ("bathroom", 1) }),
        (2, new[] { ("living", 1), ("dining", 1), ("kitchen", 1), ("hall", 1), ("bedroom", 1), ("bathroom", 1) }),
        (3, new[] { ("living", 1), ("dining", 1), ("kitchen", 1), ("hall", 1), ("bedroom", 2), ("bathroom", 1), ("toilet", 1), ("laundry", 1) }),
        (4, new[] { ("living", 1), ("dining", 1), ("kitchen", 1), ("hall", 1), ("bedroom", 2), ("bathroom", 1), ("toilet", 1), ("laundry", 1) }),
        (5, new[] { ("living", 1), ("dining", 1), ("kitchen", 1), ("hall", 1), ("bedroom", 3), ("bathroom", 2), ("toilet", 1), ("laundry", 1) }),
        (8, new[] { ("living", 1), ("dining", 1), ("kitchen", 1), ("hall", 1), ("bedroom", 4), ("bathroom", 2), ("toilet", 1), ("laundry", 1) }),
    };

    // Kind, required, minimum, target, maximum (null when open-ended).
    static readonly (string Kind, bool Required, double Min, double Target, double? Max)[] Areas =
    {
        ("living", true, 9, 18, null), ("dining", true, 9, 12, null), ("kitchen", true, 5, 8, 14), ("hall", true, 3, 5, null),
        ("bedroom", true, 9, 11, 16), ("bathroom", true, 3, 4.5, 8), ("toilet", false, 1.5, 2, 3), ("laundry", false, 2.5, 4, null),
        ("study", false, 7.5, 9, 12), ("garage", false, 15, 18, 36),
    };

    // CABA cited minimum-area rule ids the programme minimums must not go below.
    static readonly (string Kind, string Rule)[] CabaFloors =
    {
        ("living", "living-area-min"), ("dining", "dining-area-min"), ("kitchen", "kitchen-area-min"), ("bedroom", "bedroom-area-min"),
        ("bathroom", "bathroom-area-min"), ("study", "estudio-area-min"),
    };

    static string Show(TypologyProgramme programme) => string.Join("; ", programme.Issues.Select(issue => issue.Code + "@" + issue.Subject + " " + issue.Detail));

    static string Rooms(TypologyProgramme programme) => string.Join(",", programme.Rooms.Select(room => room.Kind + "x" + room.Count.ToString(CultureInfo.InvariantCulture)));

    static string Adjacencies(IEnumerable<BriefAdjacency> adjacencies) =>
        string.Join(",", adjacencies.Select(adjacency => adjacency.A + "-" + adjacency.B + ":" + BriefSerializer.Kind(adjacency.Kind)));

    // Exactly one issue, with this code and subject, and nothing else in the result.
    static bool Fails(TypologyProgramme programme, string code, string subject) =>
        programme.Issues.Count == 1 && programme.Brief is null && programme.Brief is null && programme.Rooms.Count == 0 && programme.Adjacencies.Count == 0
        && programme.Issues.Any(issue => issue.Code == code && issue.Subject == subject);

    static bool Valid(TypologyProgramme programme) => programme.Brief is { } brief && BriefValidator.Validate(brief).Length == 0;

    static string BriefIssues(TypologyProgramme programme) =>
        programme.Brief is { } brief ? string.Join("; ", BriefValidator.Validate(brief).Select(issue => issue.Code + "@" + issue.Field)) : "no brief";

    static TypologyProgramme Safe(Func<TypologyProgramme> resolve)
    {
        try { return resolve(); }
        catch (Exception error) { return new TypologyProgramme("", 0, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null, new[] { new SkillIssue("unexpected", error.GetType().Name, error.Message) }); }
    }

    public static void Run(string root, Action<bool, string, string> report)
    {
        void check(bool condition, string name, string detail = "") => report(condition, "typology: " + name, detail);
        var packPath = Path.Combine(root, "skills", Pack, "skill.json");
        SkillCatalog catalog;
        try { catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills")); }
        catch (SkillException error) { check(false, "skills/ loads", error.Code + " " + error.File + " at " + error.Field); return; }
        var found = catalog.Find(Pack);
        var caba = catalog.Find(Caba);
        check(found is { Kind: SkillKind.Typology, Priority: 20, Rules.Count: 0, Parameters.Count: 0 } && caba is not null, "the vivienda unifamiliar pack loads through SkillLoader as a typology pack");
        if (found is null || caba is null) return;
        SkillManifest pack = found;
        check(pack.Description.Contains("not a building code", StringComparison.Ordinal) && pack.Description.Contains("illustrative", StringComparison.Ordinal)
            && pack.Defaults.Any(entry => entry.Key == "programme.source_confidence" && entry.Value == SkillValue.Of("illustrative")),
            "the pack says its values are illustrative and not a building code");
        check(pack.Defaults.All(entry => entry.Key.StartsWith("programme.", StringComparison.Ordinal)), "the pack only states programme.* defaults");

        // ---- Programme values: required/optional and areas; minimums never below the CABA cited minimums ----
        var four = Safe(() => SkillTypology.Resolve(pack, 4, new[] { "study", "garage" }));
        check(four.Succeeded, "a family of four with study and garage resolves", Show(four));
        foreach (var (kind, required, min, target, max) in Areas)
        {
            var room = four.Rooms.FirstOrDefault(candidate => candidate.Kind == kind);
            check(room is not null && room.Required == required && room.MinAreaSquareMetres == min && room.TargetAreaSquareMetres == target && room.MaxAreaSquareMetres == max
                && room.Source == new SkillTrace(Pack, SkillTrace.DefaultOrigin, "programme.room." + kind + ".min_area_m2"),
                "room " + kind + " is " + (required ? "required" : "optional") + " with its areas and traced to the pack", room?.ToString() ?? "absent");
        }
        foreach (var (kind, rule) in CabaFloors)
        {
            var floor = caba.Rules.FirstOrDefault(candidate => candidate.Id == rule)?.Check.Value;
            var room = four.Rooms.FirstOrDefault(candidate => candidate.Kind == kind);
            check(floor is { Kind: SkillValueKind.Number } && room is not null && room.MinAreaSquareMetres >= floor.Number,
                "room " + kind + " minimum is not below CABA " + rule, (floor?.ToString() ?? "no rule") + " vs " + (room?.MinAreaSquareMetres.ToString(CultureInfo.InvariantCulture) ?? "absent"));
        }
        check(SkillTypology.RoomKinds.SequenceEqual(new[] { "living", "dining", "kitchen", "hall", "bedroom", "bathroom", "toilet", "laundry", "study", "garage", "storage" }),
            "the room kinds are a closed, ordered set", string.Join(",", SkillTypology.RoomKinds));

        // ---- Family size drives the counts and the optional rooms ----
        for (var size = 1; size <= 8; size++)
        {
            var any = Safe(() => SkillTypology.Resolve(pack, size, new[] { "study", "garage" }));
            check(any.Succeeded && Valid(any), "every family size 1..8 with every optional room yields a valid brief (" + size.ToString(CultureInfo.InvariantCulture) + ")", Show(any) + " " + BriefIssues(any));
        }
        foreach (var (size, rooms) in Programmes)
        {
            var programme = Safe(() => SkillTypology.Resolve(pack, size));
            var expected = string.Join(",", rooms.Select(room => room.Kind + "x" + room.Count.ToString(CultureInfo.InvariantCulture)));
            check(programme.Succeeded && programme.FamilySize == size && programme.PackId == Pack && Rooms(programme) == expected,
                "a family of " + size.ToString(CultureInfo.InvariantCulture) + " gets " + expected, Rooms(programme) + " " + Show(programme));
            check(Valid(programme), "the brief for a family of " + size.ToString(CultureInfo.InvariantCulture) + " passes BriefValidator", BriefIssues(programme));
        }
        var withStudy = Safe(() => SkillTypology.Resolve(pack, 2, new[] { "study" }));
        check(withStudy.Succeeded && Rooms(withStudy) == "livingx1,diningx1,kitchenx1,hallx1,bedroomx1,bathroomx1,studyx1", "an optional room joins on request", Rooms(withStudy));
        var both = Safe(() => SkillTypology.Resolve(pack, 2, new[] { "garage", "study", "study" }));
        check(both.Succeeded && Rooms(both) == "livingx1,diningx1,kitchenx1,hallx1,bedroomx1,bathroomx1,studyx1,garagex1", "requested rooms keep the kind order and duplicates collapse", Rooms(both));
        var alreadyIn = Safe(() => SkillTypology.Resolve(pack, 4, new[] { "toilet", "bedroom" }));
        check(alreadyIn.Succeeded && Rooms(alreadyIn) == Rooms(Safe(() => SkillTypology.Resolve(pack, 4))), "requesting a room already in the programme changes nothing", Rooms(alreadyIn));

        // ---- The brief: consumable by the brief and planner code ----
        var brief = four.Brief;
        check(brief is not null && BriefValidator.Validate(brief).Length == 0, "the brief passes BriefValidator",
            brief is null ? "no brief" : string.Join("; ", BriefValidator.Validate(brief).Select(issue => issue.Code + "@" + issue.Field)));
        if (brief is not null)
        {
            check(brief.Version == ProjectBrief.CurrentVersion && brief.Name == Pack + " programme for a family of 4", "the brief is v1 and names the pack and the family size", brief.Name);
            check(brief.Rooms.Select(room => room.Id).SequenceEqual(four.Rooms.Select(room => room.Kind)) && brief.Rooms.All(room => room.Provenance is { Source: BriefSource.Inferred } p
                && p.Reference is not null && p.Reference.Contains(Pack, StringComparison.Ordinal)),
                "brief rooms follow the programme and carry inferred provenance naming the pack");
            var bedroom = brief.Rooms.FirstOrDefault(room => room.Id == "bedroom");
            check(bedroom is { Name: "Bedroom", EffectiveCount: 2 } && bedroom.MinAreaSquareMetres?.Value == 9 && bedroom.TargetAreaSquareMetres?.Value == 11 && bedroom.MaxAreaSquareMetres?.Value == 16,
                "the bedroom brief room has count, minimum, target and maximum", bedroom?.ToString() ?? "absent");
            var living = brief.Rooms.FirstOrDefault(room => room.Id == "living");
            check(living is { Name: "Living", MaxAreaSquareMetres: null } && living.Name.StartsWith("Living", StringComparison.Ordinal), "an open-ended room has no maximum and a CABA-matching name");
            check(brief.Rooms.Select(room => room.Name).SequenceEqual(new[] { "Living", "Dining", "Kitchen", "Hall", "Bedroom", "Bathroom", "Toilet", "Laundry", "Estudio", "Garage" }),
                "brief room names match the CABA name patterns (the study is Estudio)", string.Join(",", brief.Rooms.Select(room => room.Name)));
        }
        check(Adjacencies(four.Adjacencies) == "bathroom-bedroom:preferred,bedroom-kitchen:forbidden,dining-kitchen:required,dining-living:required,hall-living:required,hall-toilet:preferred,kitchen-laundry:preferred"
            && four.Brief is not null && Adjacencies(four.Brief.Adjacencies) == Adjacencies(four.Adjacencies),
            "adjacency wishes become ordered brief adjacencies", Adjacencies(four.Adjacencies));
        var two = Safe(() => SkillTypology.Resolve(pack, 2));
        check(Adjacencies(two.Adjacencies) == "bathroom-bedroom:preferred,bedroom-kitchen:forbidden,dining-kitchen:required,dining-living:required,hall-living:required",
            "adjacencies to rooms outside the programme are left out", Adjacencies(two.Adjacencies));

        // ---- Determinism ----
        var reparsed = SkillParser.Parse(File.ReadAllText(packPath));
        var previous = CultureInfo.CurrentCulture;
        string? german = null;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            german = Safe(() => SkillTypology.Resolve(reparsed, 4, new[] { "garage", "study" })).Brief is { } b ? BriefSerializer.Serialize(b) : null;
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var first = four.Brief is null ? null : BriefSerializer.Serialize(four.Brief);
        check(first is not null && german == first && Safe(() => SkillTypology.Resolve(pack, 4, new[] { "study", "garage" })).Brief is { } again && BriefSerializer.Serialize(again) == first,
            "resolution is deterministic across calls, re-parses, request order and cultures");

        // ---- Composed resolution ----
        var alone = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Pack) });
        var fromResolution = Safe(() => SkillTypology.Resolve(alone, 4, new[] { "study", "garage" }));
        check(fromResolution.Succeeded && fromResolution.PackId == Pack && fromResolution.Brief is not null && first is not null && BriefSerializer.Serialize(fromResolution.Brief) == first,
            "resolving from a skill resolution equals resolving from the manifest", Show(fromResolution));
        var composed = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Caba), new SkillRequest(RoomQa), new SkillRequest(Dwelling), new SkillRequest(Pack) });
        var composedProgramme = Safe(() => SkillTypology.Resolve(composed, 4));
        check(composed.Succeeded && composedProgramme.Succeeded && composedProgramme.PackId == Dwelling + "+" + Pack && Rooms(composedProgramme) == Rooms(Safe(() => SkillTypology.Resolve(pack, 4))),
            "the pack composes with the CABA, room QA and dwelling packs; the dwelling bedroom minimum agrees", Show(composedProgramme));
        var strict = SkillParser.Parse("{\"manifest_version\":1,\"id\":\"bim.test.strict-bedroom\",\"version\":\"1.0.0\",\"kind\":\"qa\",\"description\":\"Fixture.\",\"priority\":10,\"defaults\":{\"room.bedroom.min_area_m2\":10}}");
        var strictCatalog = new SkillCatalog(new[] { pack, strict });
        var strictProgramme = Safe(() => SkillTypology.Resolve(SkillResolver.Resolve(strictCatalog, new[] { new SkillRequest(Pack), new SkillRequest(strict.Id) }), 4));
        check(Fails(strictProgramme, SkillTypologyCodes.MinimumConflict, "bedroom"), "a composed room minimum above the programme minimum is a typed conflict", Show(strictProgramme));
        var textual = SkillParser.Parse("{\"manifest_version\":1,\"id\":\"bim.test.textual-bedroom\",\"version\":\"1.0.0\",\"kind\":\"qa\",\"description\":\"Fixture.\",\"priority\":10,\"defaults\":{\"room.bedroom.min_area_m2\":\"12\"}}");
        var textualProgramme = Safe(() => SkillTypology.Resolve(SkillResolver.Resolve(new SkillCatalog(new[] { pack, textual }), new[] { new SkillRequest(Pack), new SkillRequest(textual.Id) }), 4));
        check(Fails(textualProgramme, SkillTypologyCodes.SettingInvalid, "room.bedroom.min_area_m2"), "a non-numeric composed room minimum is refused, not skipped", Show(textualProgramme));
        var dwellingDocument = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "skills", Dwelling, "skill.json")))!.AsObject();
        dwellingDocument["parameters"]![0]!["default"] = 12;
        var strictDwelling = SkillParser.Parse(dwellingDocument.ToJsonString());
        var parameterProgramme = Safe(() => SkillTypology.Resolve(SkillResolver.Resolve(new SkillCatalog(new[] { pack, strictDwelling }), new[] { new SkillRequest(Pack), new SkillRequest(Dwelling) }), 4));
        check(Fails(parameterProgramme, SkillTypologyCodes.MinimumConflict, "bedroom") && parameterProgramme.Issues[0].Detail.Contains(Dwelling + "/bedroom-min-area", StringComparison.Ordinal),
            "a composed min-area rule whose parameter exceeds the programme minimum is a typed conflict", Show(parameterProgramme));
        var literalRule = SkillParser.Parse("{\"manifest_version\":1,\"id\":\"bim.test.study-rule\",\"version\":\"1.0.0\",\"kind\":\"qa\",\"description\":\"Fixture.\",\"priority\":10,\"rules\":[{\"id\":\"estudio-area\",\"severity\":\"error\",\"target\":{\"room\":\"estudio*\"},\"check\":{\"op\":\"min\",\"property\":\"area_m2\",\"value\":8},\"message\":\"Fixture.\"}]}");
        var literalProgramme = Safe(() => SkillTypology.Resolve(SkillResolver.Resolve(new SkillCatalog(new[] { pack, literalRule }), new[] { new SkillRequest(Pack), new SkillRequest(literalRule.Id) }), 2));
        check(Fails(literalProgramme, SkillTypologyCodes.MinimumConflict, "study"), "a composed literal min-area rule matching the Estudio name above the programme minimum is a conflict", Show(literalProgramme));
        var withCabaStudy = Safe(() => SkillTypology.Resolve(composed, 4, new[] { "study", "garage" }));
        check(withCabaStudy.Succeeded && Valid(withCabaStudy), "with CABA composed, the Estudio room meets the CABA estudio minimum", Show(withCabaStudy));
        var anonymous = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject();
        anonymous["id"] = "bim.test.programme-qa";
        anonymous["kind"] = "qa";
        var anonymousPack = SkillParser.Parse(anonymous.ToJsonString());
        var anonymousProgramme = Safe(() => SkillTypology.Resolve(SkillResolver.Resolve(new SkillCatalog(new[] { anonymousPack }), new[] { new SkillRequest(anonymousPack.Id) }), 4));
        check(anonymousProgramme.Succeeded && anonymousProgramme.PackId.Length == 0 && anonymousProgramme.Brief is { Name: "typology programme for a family of 4" } && Valid(anonymousProgramme),
            "a programme without a typology skill names its brief without a leading space", anonymousProgramme.Brief?.Name ?? Show(anonymousProgramme));
        var lenient = SkillParser.Parse("{\"manifest_version\":1,\"id\":\"bim.test.lenient-bedroom\",\"version\":\"1.0.0\",\"kind\":\"qa\",\"description\":\"Fixture.\",\"priority\":10,\"defaults\":{\"room.bedroom.min_area_m2\":7.5}}");
        var lenientProgramme = Safe(() => SkillTypology.Resolve(SkillResolver.Resolve(new SkillCatalog(new[] { pack, lenient }), new[] { new SkillRequest(Pack), new SkillRequest(lenient.Id) }), 4));
        check(lenientProgramme.Succeeded && lenientProgramme.Rooms.First(room => room.Kind == "bedroom").MinAreaSquareMetres == 9, "a composed room minimum below the programme minimum keeps the programme minimum", Show(lenientProgramme));
        var failedResolution = SkillResolver.Resolve(catalog, new[] { new SkillRequest("bim.test.absent") });
        check(Fails(Safe(() => SkillTypology.Resolve(failedResolution, 4)), SkillTypologyCodes.ResolutionFailed, ""), "a failed skill resolution is refused", Show(Safe(() => SkillTypology.Resolve(failedResolution, 4))));

        // ---- Typed refusals ----
        TypologyProgramme Variant(Action<JsonObject> edit, int size = 4, IReadOnlyCollection<string>? include = null)
        {
            var document = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject();
            edit(document["defaults"]!.AsObject());
            SkillManifest manifest;
            try { manifest = SkillParser.Parse(document.ToJsonString()); }
            catch (SkillException error) { return new TypologyProgramme("", 0, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null, new[] { new SkillIssue("parse " + error.Code, error.Field ?? "", error.Detail ?? "") }); }
            return Safe(() => SkillTypology.Resolve(manifest, size, include));
        }
        foreach (var size in new[] { 0, -1, 9 })
        {
            var outside = Safe(() => SkillTypology.Resolve(pack, size));
            check(Fails(outside, SkillTypologyCodes.FamilySizeInvalid, "family_size"), "family size " + size.ToString(CultureInfo.InvariantCulture) + " outside 1..8 is refused", Show(outside));
        }
        var sauna = Variant(defaults => { defaults["programme.room.sauna.required"] = false; defaults["programme.room.sauna.min_area_m2"] = 4; defaults["programme.room.sauna.target_area_m2"] = 5; });
        check(Fails(sauna, SkillTypologyCodes.RoomKindUnknown, "sauna"), "an unknown room kind in the pack is refused", Show(sauna));
        var saunaAdjacency = Variant(defaults => defaults["programme.adjacency.living.sauna"] = "preferred");
        check(Fails(saunaAdjacency, SkillTypologyCodes.RoomKindUnknown, "sauna"), "an adjacency to an unknown room kind is refused", Show(saunaAdjacency));
        var requestedSauna = Safe(() => SkillTypology.Resolve(pack, 4, new[] { "sauna" }));
        check(Fails(requestedSauna, SkillTypologyCodes.RoomKindUnknown, "sauna"), "requesting an unknown room kind is refused", Show(requestedSauna));
        var storage = Safe(() => SkillTypology.Resolve(pack, 4, new[] { "storage" }));
        check(Fails(storage, SkillTypologyCodes.RoomNotOffered, "storage"), "requesting a known room kind the pack does not offer is refused", Show(storage));
        var minAboveTarget = Variant(defaults => defaults["programme.room.kitchen.min_area_m2"] = 9);
        check(Fails(minAboveTarget, SkillTypologyCodes.MinimumConflict, "kitchen"), "a minimum above the target is a minimum conflict", Show(minAboveTarget));
        var minAboveMax = Variant(defaults => { defaults["programme.room.bathroom.min_area_m2"] = 9; defaults["programme.room.bathroom.target_area_m2"] = 9; });
        check(Fails(minAboveMax, SkillTypologyCodes.MinimumConflict, "bathroom"), "a minimum above the maximum is a minimum conflict", Show(minAboveMax));
        var targetAboveMax = Variant(defaults => defaults["programme.room.study.target_area_m2"] = 13, 4, new[] { "study" });
        check(Fails(targetAboveMax, SkillTypologyCodes.MinimumConflict, "study"), "a target above the maximum is a minimum conflict", Show(targetAboveMax));
        var optionalConflict = Variant(defaults => defaults["programme.room.garage.min_area_m2"] = 40);
        check(Fails(optionalConflict, SkillTypologyCodes.MinimumConflict, "garage"), "an excluded optional room is still checked", Show(optionalConflict));
        var bothIssues = Variant(defaults => { defaults["programme.room.kitchen.min_area_m2"] = 9; defaults["programme.room.sauna.required"] = true; });
        check(bothIssues.Issues.Select(issue => issue.Code + "@" + issue.Subject).SequenceEqual(new[] { SkillTypologyCodes.MinimumConflict + "@kitchen", SkillTypologyCodes.RoomKindUnknown + "@sauna" }),
            "every issue is reported, in setting key order", Show(bothIssues));
        foreach (var (name, edit, subject) in new (string, Action<JsonObject>, string)[]
        {
            ("an unknown room attribute", defaults => defaults["programme.room.kitchen.colour"] = "red", "programme.room.kitchen.colour"),
            ("a non-boolean required flag", defaults => defaults["programme.room.kitchen.required"] = "yes", "programme.room.kitchen.required"),
            ("a missing minimum area", defaults => defaults.Remove("programme.room.hall.min_area_m2"), "programme.room.hall.min_area_m2"),
            ("a missing required flag", defaults => defaults.Remove("programme.room.hall.required"), "programme.room.hall.required"),
            ("a non-positive minimum area", defaults => defaults["programme.room.hall.min_area_m2"] = 0, "programme.room.hall.min_area_m2"),
            ("a textual target area", defaults => defaults["programme.room.hall.target_area_m2"] = "5", "programme.room.hall.target_area_m2"),
            ("a non-positive persons per room", defaults => defaults["programme.room.bedroom.persons_per_room"] = 0, "programme.room.bedroom.persons_per_room"),
            ("a fractional fixed count", defaults => defaults["programme.room.hall.count"] = 1.5, "programme.room.hall.count"),
            ("a count with persons per room", defaults => defaults["programme.room.bedroom.count"] = 2, "programme.room.bedroom.count"),
            ("a required room with an inclusion threshold", defaults => defaults["programme.room.kitchen.include_from_persons"] = 2, "programme.room.kitchen.include_from_persons"),
            ("an unknown adjacency relation", defaults => defaults["programme.adjacency.dining.living"] = "nearby", "programme.adjacency.dining.living"),
            ("a self adjacency", defaults => defaults["programme.adjacency.living.living"] = "preferred", "programme.adjacency.living.living"),
            ("an adjacency stated in both orders", defaults => defaults["programme.adjacency.living.dining"] = "required", "programme.adjacency.living.dining"),
            ("an adjacency to a room the pack does not offer", defaults => defaults["programme.adjacency.living.storage"] = "preferred", "programme.adjacency.living.storage"),
            ("a malformed adjacency key", defaults => defaults["programme.adjacency.living"] = "preferred", "programme.adjacency.living"),
            ("an unknown programme setting", defaults => defaults["programme.colour"] = "red", "programme.colour"),
            ("a family size bound below one", defaults => defaults["programme.family_size.min"] = 0, "programme.family_size.min"),
            ("inverted family size bounds", defaults => defaults["programme.family_size.min"] = 9, "programme.family_size.min"),
        })
        {
            var refused = Variant(edit);
            check(Fails(refused, SkillTypologyCodes.SettingInvalid, subject), name + " is refused as a setting issue", Show(refused));
        }
        var noProgramme = Safe(() => SkillTypology.Resolve(catalog.Find(Dwelling)!, 4));
        check(Fails(noProgramme, SkillTypologyCodes.ProgrammeMissing, Dwelling), "a typology pack without programme.room settings is refused", Show(noProgramme));
        var qaPack = Safe(() => SkillTypology.Resolve(catalog.Find(RoomQa)!, 4));
        check(Fails(qaPack, SkillTypologyCodes.KindMismatch, RoomQa), "a non-typology pack is refused", Show(qaPack));
        var looseSize = Variant(defaults => { defaults.Remove("programme.family_size.min"); defaults.Remove("programme.family_size.max"); }, 12);
        check(looseSize.Succeeded && Rooms(looseSize).Contains("bedroomx6", StringComparison.Ordinal) && Valid(looseSize), "without family size bounds any positive family size resolves to a valid brief",
            Show(looseSize) + " " + BriefIssues(looseSize));

        // ---- Room count cap (SkillTypology.MaxRoomCount) ----
        check(SkillTypology.MaxRoomCount == 50, "rooms of one kind are capped at 50");
        var tinyShare = Variant(defaults => defaults["programme.room.bedroom.persons_per_room"] = 1e-9);
        check(Fails(tinyShare, SkillTypologyCodes.SettingInvalid, "programme.room.bedroom.persons_per_room"), "a persons per room giving more than 50 rooms is refused, not overflowed", Show(tinyShare));
        var tinyOptional = Variant(defaults => { defaults["programme.room.garage.persons_per_room"] = 0.01; }, 2);
        check(Fails(tinyOptional, SkillTypologyCodes.SettingInvalid, "programme.room.garage.persons_per_room"), "the cap applies to optional rooms outside the programme too", Show(tinyOptional));
        var manyFixed = Variant(defaults => defaults["programme.room.hall.count"] = 51);
        check(Fails(manyFixed, SkillTypologyCodes.SettingInvalid, "programme.room.hall.count"), "a fixed count above 50 is refused", Show(manyFixed));
        var hugeFixed = Variant(defaults => defaults["programme.room.hall.count"] = 1e300);
        check(Fails(hugeFixed, SkillTypologyCodes.SettingInvalid, "programme.room.hall.count"), "a huge fixed count is refused", Show(hugeFixed));
        var fiftyFixed = Variant(defaults => defaults["programme.room.hall.count"] = 50);
        check(fiftyFixed.Succeeded && fiftyFixed.Rooms.First(room => room.Kind == "hall").Count == 50 && Valid(fiftyFixed), "a fixed count of 50 is accepted", Show(fiftyFixed));
        var unbounded = Variant(defaults => { defaults.Remove("programme.family_size.min"); defaults.Remove("programme.family_size.max"); }, 101);
        check(Fails(unbounded, SkillTypologyCodes.SettingInvalid, "programme.room.bedroom.persons_per_room"), "a family large enough to need more than 50 bedrooms is refused", Show(unbounded));
        check(Fails(Variant(defaults => { defaults.Remove("programme.family_size.min"); defaults.Remove("programme.family_size.max"); }, 0), SkillTypologyCodes.FamilySizeInvalid, "family_size"),
            "a family size below one is always refused");
        var fixedCount = Variant(defaults => defaults["programme.room.hall.count"] = 2);
        check(fixedCount.Succeeded && fixedCount.Rooms.First(room => room.Kind == "hall").Count == 2, "a fixed count is honoured", Show(fixedCount));
    }
}
