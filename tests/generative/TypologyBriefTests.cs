using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bim.Core;
using Bim.Generative;

// P5-11 typology programme into the prototype planner oracle: the checked-in bim.typology.vivienda-unifamiliar pack and a
// family size become the planner brief with no hand-written brief (the rooms, counts, areas and adjacencies are the
// P5-07 programme, checked here against an independent reading of the pack file), the brief is planned and ranked on a
// lot under the CABA minimums of ar.caba.codigo-edificacion (PrototypeTemplates.Rules), the same pack + size + lot +
// seed always give byte-identical prototypes (across runs, re-parses, request order and cultures), unknown room kinds,
// family sizes and non-typology packs are typed refusals, and the existing apartment generator output is not affected.
// Unit-tested evidence only.
static class TypologyBriefTests
{
    const string Pack = "bim.typology.vivienda-unifamiliar";
    const string Caba = "ar.caba.codigo-edificacion";
    const double Tolerance = 1e-9;

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "typology-brief: " + name, detail);

        var catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills"));
        var pack = catalog.Find(Pack);
        var caba = catalog.Find(Caba);
        Check(pack is { Kind: SkillKind.Typology } && caba is not null, "the vivienda unifamiliar typology pack and the CABA code pack load from skills/");
        if (pack is null || caba is null) return;
        var packPath = Path.Combine(root, "skills", Pack, "skill.json");
        var defaults = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject()["defaults"]!.AsObject();

        // ---- the brief is the programme of the pack for the family size, on the lot ----
        var lot = new PrototypeLot(12, 12);
        var four = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 6)));
        Check(four.Programme.Succeeded && four.Programme.PackId == Pack && four.Programme.FamilySize == 4, "family 4 resolves the pack programme", Show(four));
        var expected = ExpectedRooms(defaults, 4, Array.Empty<string>());
        Check(four.Brief is not null && Rooms(four.Brief) == expected,
            "the family-4 brief rooms are the pack programme read independently from skill.json", (four.Brief is null ? "no brief" : Rooms(four.Brief)) + " vs " + expected);
        Check(expected == "living:Living:1:9/18/- dining:Dining:1:9/12/- kitchen:Kitchen:1:5/8/14 hall:Hall:1:3/5/- bedroom:Bedroom:2:9/11/16 bathroom:Bathroom:1:3/4.5/8 toilet:Toilet:1:1.5/2/3 laundry:Laundry:1:2.5/4/-",
            "family 4: one living, dining, kitchen, hall, bathroom, toilet and laundry and two bedrooms", expected);
        var programmeBrief = four.Programme.Brief;
        Check(four.Brief is not null && programmeBrief is not null && four.Brief.Rooms.SequenceEqual(programmeBrief.Rooms) && four.Brief.Adjacencies.SequenceEqual(programmeBrief.Adjacencies),
            "the planner brief keeps the SkillTypology rooms and adjacencies exactly (no hand-written brief)");
        Check(four.Brief is not null && Adjacencies(four.Brief) == ExpectedAdjacencies(defaults, four.Brief),
            "the brief adjacencies are the pack adjacency wishes between rooms of the programme", four.Brief is null ? "" : Adjacencies(four.Brief));
        Check(four.Brief is { Constraints.Site: { } site } && site.WidthMetres.Value == 12 && site.DepthMetres.Value == 12 && four.Brief.Constraints.Levels?.Value == 1,
            "the brief site is the lot on one level");
        Check(four.Brief is not null && BriefValidator.Validate(four.Brief).All(issue => issue.Severity != BriefSeverity.Error),
            "the planner brief passes BriefValidator", four.Brief is null ? "" : string.Join("; ", BriefValidator.Validate(four.Brief).Select(issue => issue.Code + "@" + issue.Field)));
        Check(four.Brief is not null && four.Brief.Name.StartsWith(Pack, StringComparison.Ordinal) && four.Brief.Name.Contains("family of 4", StringComparison.Ordinal)
            && four.Brief.Name.Contains("12 x 12", StringComparison.Ordinal), "the brief name states the pack, the family size and the lot", four.Brief?.Name ?? "");
        var direct = four.Programme.Succeeded ? SafeBrief(() => TypologyBrief.Brief(four.Programme, lot)) : null;
        Check(direct is not null && four.Brief is not null && BriefSerializer.Serialize(direct) == BriefSerializer.Serialize(four.Brief),
            "TypologyBrief.Brief gives the same brief Generate plans");

        // ---- the brief is planned and ranked into prototypes ----
        Check(four.Status == PrototypeStatus.Ranked && four.Refusals.Count == 0 && four.Alternatives is { Status: AlternativeSetStatus.Ranked } && four.Alternatives.Ranked.Count >= 1,
            "family 4 on a 12 x 12 m lot gives ranked prototypes", Show(four));
        Check(four.Candidates.Count == 6 && four.Candidates.Select((candidate, index) => candidate.Index == index && candidate.Seed == index && candidate.Strategy == PrototypeStrategies.WholeLot).All(ok => ok)
            && four.Alternatives?.Requested == 6, "six whole-lot candidates with seeds 0..5", four.Candidates.Count.ToString(CultureInfo.InvariantCulture));
        if (four.Brief is not null && four.Alternatives is not null)
            foreach (var alternative in four.Alternatives.Ranked)
            {
                var problem = PlanProblem(four.Brief, alternative.Plan, lot);
                Check(problem is null, "ranked prototype seed " + alternative.Seed.ToString(CultureInfo.InvariantCulture) + " holds every programme room in the lot within its range and the CABA minimums", problem ?? "");
            }

        // ---- determinism: same pack + size + lot + seed give the same prototypes ----
        var firstDigest = Digest(four);
        var again = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 6)));
        var reparsed = SkillParser.Parse(File.ReadAllText(packPath));
        var fromReparsed = Safe(() => TypologyBrief.Generate(reparsed, new TypologyBriefRequest(4, lot, 6)));
        string german;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            german = Digest(Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 6))));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(four.Alternatives is not null && firstDigest == Digest(again) && firstDigest == Digest(fromReparsed) && firstDigest == german,
            "same pack + family size + lot + seed give byte-identical prototypes across runs, re-parses and cultures", firstDigest);
        var resolution = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Pack) });
        var fromResolution = Safe(() => TypologyBrief.Generate(resolution, new TypologyBriefRequest(4, lot, 6)));
        Check(fromResolution.Status == PrototypeStatus.Ranked && Digest(fromResolution) == firstDigest, "planning from a skill resolution equals planning from the manifest", Show(fromResolution));
        var composed = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack) });
        var fromComposed = Safe(() => TypologyBrief.Generate(composed, new TypologyBriefRequest(4, lot, 6)));
        Check(composed.Succeeded && fromComposed.Programme.Succeeded && fromComposed.Brief is not null && four.Brief is not null && fromComposed.Brief.Rooms.SequenceEqual(four.Brief.Rooms)
            && fromComposed.Status == PrototypeStatus.Ranked, "composed with the CABA pack the programme rooms are unchanged and planned", Show(fromComposed));
        var studyGarage = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 4, Include: new[] { "study", "garage" })));
        var garageStudy = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 4, Include: new[] { "garage", "study", "garage" })));
        Check(studyGarage.Brief is not null && Rooms(studyGarage.Brief) == ExpectedRooms(defaults, 4, new[] { "study", "garage" }) && Digest(studyGarage) == Digest(garageStudy),
            "requested optional rooms join the brief and their order does not matter", studyGarage.Brief is null ? Show(studyGarage) : Rooms(studyGarage.Brief));
        var seeded = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 3, Seed: 40)));
        Check(seeded.Candidates.Select(candidate => candidate.Seed).SequenceEqual(new[] { 40, 41, 42 }) && Digest(seeded) == Digest(Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 3, Seed: 40)))),
            "candidate seeds are Seed + i and a seeded request is deterministic");

        // ---- every family size of the pack gives the programme brief ----
        var bigLot = new PrototypeLot(16, 16);
        var ranked = new List<int>();
        for (var size = 1; size <= 8; size++)
        {
            var result = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(size, bigLot, 3)));
            var want = ExpectedRooms(defaults, size, Array.Empty<string>());
            Check(result.Status != PrototypeStatus.Refused && result.Brief is not null && Rooms(result.Brief) == want,
                "family " + size.ToString(CultureInfo.InvariantCulture) + " gives the pack programme as the planner brief", result.Brief is null ? Show(result) : Rooms(result.Brief) + " vs " + want);
            if (result.Status == PrototypeStatus.Ranked) ranked.Add(size);
        }
        Check(ranked.Contains(1) && ranked.Contains(2) && ranked.Contains(4), "families 1, 2 and 4 are planned into ranked prototypes on a 16 x 16 m lot",
            string.Join(",", ranked.Select(size => size.ToString(CultureInfo.InvariantCulture))));

        // ---- typed refusals ----
        var sauna = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 4, Include: new[] { "sauna" })));
        Check(IsRefused(sauna, SkillTypologyCodes.RoomKindUnknown, "sauna"), "a requested unknown room kind is a typed refusal", Show(sauna));
        var saunaDocument = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject();
        saunaDocument["defaults"]!["programme.room.sauna.min_area_m2"] = 4;
        saunaDocument["defaults"]!["programme.room.sauna.required"] = true;
        var saunaPack = SkillParser.Parse(saunaDocument.ToJsonString());
        var unknownKind = Safe(() => TypologyBrief.Generate(saunaPack, new TypologyBriefRequest(4, lot, 4)));
        Check(IsRefused(unknownKind, SkillTypologyCodes.RoomKindUnknown, "sauna"), "a pack stating an unknown room kind is a typed refusal, nothing planned", Show(unknownKind));
        var notOffered = JsonNode.Parse(File.ReadAllText(packPath))!.AsObject();
        foreach (var key in notOffered["defaults"]!.AsObject().Select(entry => entry.Key).Where(key => key.Contains(".garage", StringComparison.Ordinal)).ToArray())
            notOffered["defaults"]!.AsObject().Remove(key);
        var noGarage = Safe(() => TypologyBrief.Generate(SkillParser.Parse(notOffered.ToJsonString()), new TypologyBriefRequest(4, lot, 4, Include: new[] { "garage" })));
        Check(IsRefused(noGarage, SkillTypologyCodes.RoomNotOffered, "garage"), "a requested room the pack does not state is a typed refusal", Show(noGarage));
        foreach (var size in new[] { 0, 9, -3 })
        {
            var bad = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(size, lot, 4)));
            Check(IsRefused(bad, SkillTypologyCodes.FamilySizeInvalid, SkillTypology.FamilySizeSubject),
                "family size " + size.ToString(CultureInfo.InvariantCulture) + " outside the pack bounds is a typed refusal", Show(bad));
        }
        var wrongKind = Safe(() => TypologyBrief.Generate(caba, new TypologyBriefRequest(4, lot, 4)));
        Check(IsRefused(wrongKind, SkillTypologyCodes.KindMismatch, Caba), "a non-typology pack is a typed refusal", Show(wrongKind));
        var badLot = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, new PrototypeLot(2, double.NaN), 4)));
        Check(IsRefused(badLot, PrototypeCodes.LotInvalid, "lot") && badLot.Programme.Succeeded, "an out-of-range lot is a typed refusal", Show(badLot));
        var badCount = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 0)));
        Check(IsRefused(badCount, PrototypeCodes.CountInvalid, "alternatives"), "an alternative count outside 1..32 is a typed refusal", Show(badCount));
        var badDoor = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(4, lot, 4, DoorWidthMetres: 0)));
        Check(IsRefused(badDoor, PrototypeCodes.OptionsInvalid, "door_width_m"), "a non-positive door width is a typed refusal", Show(badDoor));
        var both = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(0, new PrototypeLot(200, 10), 4, Include: new[] { "sauna" })));
        Check(both.Status == PrototypeStatus.Refused && both.Refusals.Any(r => r.Code == SkillTypologyCodes.FamilySizeInvalid) && both.Refusals.Any(r => r.Code == SkillTypologyCodes.RoomKindUnknown)
            && both.Refusals.Any(r => r.Code == PrototypeCodes.LotInvalid) && both.Refusals.All(r => r.Hard && !r.Satisfied), "every refusal is listed at once, each hard and unsatisfied", Show(both));
        var refusedBrief = false;
        try { TypologyBrief.Brief(sauna.Programme, lot); }
        catch (ArgumentException) { refusedBrief = true; }
        Check(refusedBrief, "TypologyBrief.Brief refuses a refused programme with ArgumentException");

        // ---- the existing apartment generator is untouched ----
        var apartment = new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 6, Seed: 0);
        var before = PrototypeSerializer.Serialize(PrototypeGenerator.Generate(apartment));
        _ = Safe(() => TypologyBrief.Generate(pack, new TypologyBriefRequest(8, bigLot, 2)));
        Check(PrototypeSerializer.Serialize(PrototypeGenerator.Generate(apartment)) == before && PrototypeTemplates.Brief(ApartmentTypology.TwoBedroom, apartment.Lot).Rooms.All(room => room.EffectiveCount == 1),
            "typology planning leaves the apartment template generator output unchanged");
    }

    static TypologyBriefResult Safe(Func<TypologyBriefResult> run)
    {
        try { return run(); }
        catch (Exception error)
        {
            var programme = new TypologyProgramme("", 0, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null, new[] { new SkillIssue("exception", error.GetType().Name, error.Message) });
            return new TypologyBriefResult(PrototypeStatus.Refused, new TypologyBriefRequest(0, new PrototypeLot(0, 0)), programme, null,
                new[] { new PlanConstraint("exception", error.GetType().Name, true, false, error.Message) }, Array.Empty<PrototypeCandidate>(), null);
        }
    }

    static ProjectBrief? SafeBrief(Func<ProjectBrief> run)
    {
        try { return run(); }
        catch (Exception) { return null; }
    }

    static bool IsRefused(TypologyBriefResult result, string code, string subject) =>
        result.Status == PrototypeStatus.Refused && result.Brief is null && result.Alternatives is null && result.Candidates.Count == 0
        && result.Refusals.Any(refusal => refusal.Code == code && refusal.Subject == subject && refusal.Hard && !refusal.Satisfied);

    static string Show(TypologyBriefResult result) => result.Status + " " + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject + " " + r.Detail).Take(6))
        + (result.Alternatives is { } set ? " ranked " + set.Ranked.Count.ToString(CultureInfo.InvariantCulture) + " " + string.Join("; ", set.Alternatives.SelectMany(a => a.Reasons).Distinct().Take(4)) : "");

    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Rooms(ProjectBrief brief) => string.Join(" ", brief.Rooms.Select(room => room.Id + ":" + room.Name + ":" + room.EffectiveCount.ToString(CultureInfo.InvariantCulture) + ":"
        + (room.MinAreaSquareMetres is { } min ? F(min.Value) : "-") + "/" + (room.TargetAreaSquareMetres is { } target ? F(target.Value) : "-") + "/" + (room.MaxAreaSquareMetres is { } max ? F(max.Value) : "-")));

    static string Adjacencies(ProjectBrief brief) => string.Join(" ", brief.Adjacencies.Select(a => a.A + "-" + a.B + ":" + a.Kind.ToString().ToLowerInvariant()));

    // Independent reading of the pack defaults: rooms in the programme kind order, included when required, requested or
    // from include_from_persons on; count fixed, ceil(family / persons_per_room) or 1.
    static string ExpectedRooms(JsonObject defaults, int family, IReadOnlyCollection<string> include)
    {
        var kinds = new[] { ("living", "Living"), ("dining", "Dining"), ("kitchen", "Kitchen"), ("hall", "Hall"), ("bedroom", "Bedroom"), ("bathroom", "Bathroom"), ("toilet", "Toilet"),
            ("laundry", "Laundry"), ("study", "Estudio"), ("garage", "Garage"), ("storage", "Storage") };
        double? Number(string key) => defaults[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
        var parts = new List<string>();
        foreach (var (kind, name) in kinds)
        {
            var prefix = "programme.room." + kind + ".";
            if (defaults[prefix + "required"] is not JsonValue requiredValue || !requiredValue.TryGetValue<bool>(out var required)) continue;
            var from = Number(prefix + "include_from_persons");
            if (!(required || include.Contains(kind) || (from is { } threshold && family >= threshold))) continue;
            var count = Number(prefix + "count") is { } fixedCount ? (int)fixedCount : Number(prefix + "persons_per_room") is { } persons ? (int)Math.Ceiling(family / persons) : 1;
            var min = Number(prefix + "min_area_m2") ?? double.NaN;
            var target = Number(prefix + "target_area_m2") ?? min;
            var max = Number(prefix + "max_area_m2");
            parts.Add(kind + ":" + name + ":" + count.ToString(CultureInfo.InvariantCulture) + ":" + F(min) + "/" + F(target) + "/" + (max is { } top ? F(top) : "-"));
        }
        return string.Join(" ", parts);
    }

    static string ExpectedAdjacencies(JsonObject defaults, ProjectBrief brief)
    {
        var present = brief.Rooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        return string.Join(" ", defaults.Where(entry => entry.Key.StartsWith("programme.adjacency.", StringComparison.Ordinal))
            .Select(entry => (Parts: entry.Key.Split('.'), Relation: entry.Value!.GetValue<string>()))
            .Select(item => (A: string.CompareOrdinal(item.Parts[2], item.Parts[3]) < 0 ? item.Parts[2] : item.Parts[3], B: string.CompareOrdinal(item.Parts[2], item.Parts[3]) < 0 ? item.Parts[3] : item.Parts[2], item.Relation))
            .Where(item => present.Contains(item.A) && present.Contains(item.B))
            .OrderBy(item => item.A, StringComparer.Ordinal).ThenBy(item => item.B, StringComparer.Ordinal)
            .Select(item => item.A + "-" + item.B + ":" + item.Relation));
    }

    // Independent check of one ranked plan: one planned room per brief room instance, inside the lot, no overlap, the
    // brief area range and the CABA minimum area and side of the room name (living 9/2.8, kitchen 3/1.5, bedroom 7.5/2.5, bath 1.4/0.9).
    static string? PlanProblem(ProjectBrief brief, SpacePlan plan, PrototypeLot lot)
    {
        if (!plan.IsFeasible) return "not feasible";
        var expected = brief.Rooms.Sum(room => room.EffectiveCount);
        if (plan.Rooms.Count != expected) return plan.Rooms.Count.ToString(CultureInfo.InvariantCulture) + " rooms, " + expected.ToString(CultureInfo.InvariantCulture) + " expected";
        foreach (var room in brief.Rooms)
            if (plan.Rooms.Count(planned => planned.RoomId == room.Id) != room.EffectiveCount) return room.Id + " count";
        var caba = new (string Prefix, double Area, double Side)[] { ("living", 9, 2.8), ("kitchen", 3, 1.5), ("bedroom", 7.5, 2.5), ("bath", 1.4, 0.9) };
        foreach (var planned in plan.Rooms)
        {
            var rect = planned.Rect;
            var room = brief.Rooms.First(candidate => candidate.Id == planned.RoomId);
            if (rect.X < -Tolerance || rect.Y < -Tolerance || rect.Right > lot.WidthMetres + Tolerance || rect.Top > lot.DepthMetres + Tolerance) return planned.InstanceId + " outside the lot";
            if (room.MinAreaSquareMetres is { } min && rect.Area < min.Value - Tolerance) return planned.InstanceId + " below its minimum area";
            if (room.MaxAreaSquareMetres is { } max && rect.Area > max.Value + Tolerance) return planned.InstanceId + " above its maximum area";
            foreach (var rule in caba)
                if (planned.Name.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase) && (rect.Area < rule.Area - Tolerance || Math.Min(rect.Width, rect.Depth) < rule.Side - Tolerance))
                    return planned.InstanceId + " below the CABA " + rule.Prefix + " minimum";
        }
        for (var i = 0; i < plan.Rooms.Count; i++)
            for (var j = i + 1; j < plan.Rooms.Count; j++)
            {
                var a = plan.Rooms[i].Rect;
                var b = plan.Rooms[j].Rect;
                if (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 1e-6 && Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y) > 1e-6) return plan.Rooms[i].InstanceId + " overlaps " + plan.Rooms[j].InstanceId;
            }
        return null;
    }

    static string Digest(TypologyBriefResult result)
    {
        var text = (result.Brief is null ? "" : BriefSerializer.Serialize(result.Brief)) + "\n"
            + (result.Alternatives is null ? "" : AlternativeSetSerializer.Serialize(result.Alternatives)) + "\n"
            + string.Join(";", result.Candidates.Select(c => c.Index.ToString(CultureInfo.InvariantCulture) + c.Strategy + c.Seed.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
