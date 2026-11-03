using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-03 apartment prototype oracle: 1/2/3-bedroom typology templates produce valid project briefs on a given lot,
// ranked layouts that honour the CABA minimums of the ar.caba.codigo-edificacion pack (read here from the skill file,
// independently of the generator), approved invariants (fixed rooms keep their exact rectangle, fixed walls stay on
// room edges) when the typology or the lot depth changes, typed refusals for out-of-range input, rejections with
// reasons for layouts that would move an approved room, and byte-identical canonical JSON for the same input and seed.
// Small briefs give equal-score mirrored layouts, so no check assumes distinct totals. Unit-tested evidence only.
static class PrototypeTests
{
    const double Tolerance = 1e-9;
    const double EdgeEpsilon = 1e-7;

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        var caba = CabaMinimums(Path.Combine(root, "skills", "ar.caba.codigo-edificacion", "skill.json"));
        Check(caba.Count >= 4, "CABA pack minimums for living, kitchen, bedroom and bath rooms are read from the skill file", string.Join(",", caba.Keys));

        // ---- templates: valid briefs for 1, 2 and 3 bedrooms on the given lot ----
        var lot = new PrototypeLot(8.66, 20);
        foreach (var (typology, bedrooms) in new[] { (ApartmentTypology.OneBedroom, 1), (ApartmentTypology.TwoBedroom, 2), (ApartmentTypology.ThreeBedroom, 3) })
        {
            var brief = PrototypeTemplates.Brief(typology, lot);
            var label = Bedrooms(bedrooms);
            Check(!BriefValidator.HasErrors(BriefValidator.Validate(brief)), label + " template brief validates",
                string.Join("; ", BriefValidator.Validate(brief).Select(i => i.Code + "@" + i.Field)));
            Check(brief.Rooms.Count(r => r.Id.StartsWith("bedroom_", StringComparison.Ordinal)) == bedrooms, label + " template has " + Text(bedrooms) + " bedrooms",
                string.Join(",", brief.Rooms.Select(r => r.Id)));
            Check(brief.Rooms.All(r => r.EffectiveCount == 1) && brief.Rooms.Any(r => r.Id == "living_dining") && brief.Rooms.Any(r => r.Id == "kitchen")
                && brief.Rooms.Any(r => r.Id == "bathroom") && brief.Rooms.Any(r => r.Id == "hall"), label + " template has single living, kitchen, bathroom and hall rooms");
            Check(brief.Constraints.Site is { } site && site.WidthMetres.Value == 8.66 && site.DepthMetres.Value == 20 && brief.Constraints.Levels?.Value == 1
                && brief.Constraints.Jurisdiction?.Value == "CABA", label + " template uses the lot as site, one level, CABA jurisdiction");
            var canonical = BriefSerializer.Serialize(brief);
            Check(BriefSerializer.Serialize(BriefParser.Parse(canonical)) == canonical, label + " template brief round-trips through the brief schema", canonical);
            Check(brief.Adjacencies.Any(a => a.Kind == BriefAdjacencyKind.Required && Pair(a, "kitchen", "living_dining"))
                && brief.Adjacencies.Any(a => a.Kind == BriefAdjacencyKind.Required && Pair(a, "hall", "living_dining")), label + " template requires kitchen and hall next to living-dining");
            Check(BriefSerializer.Serialize(PrototypeTemplates.Brief(typology, lot)) == canonical, label + " template brief is deterministic");
        }
        Check(PrototypeTemplates.Brief(ApartmentTypology.TwoBedroom, new PrototypeLot(10, 24)).Constraints.Site is { } other && other.WidthMetres.Value == 10 && other.DepthMetres.Value == 24,
            "template site follows the requested lot");

        // ---- the generator's CABA rules equal the skill pack values ----
        var rules = PrototypeTemplates.CabaRules.Rules;
        Check(rules.Count >= 4 && rules.All(r => r.NamePattern is not null && r.Reference is { Length: > 0 } reference && reference.Contains("ar.caba.codigo-edificacion", StringComparison.Ordinal)),
            "prototype CABA rules are name-pattern rules citing the ar.caba.codigo-edificacion pack");
        foreach (var rule in rules)
        {
            var known = caba.TryGetValue(rule.NamePattern ?? "", out var minimum);
            Check(known && rule.MinAreaSquareMetres == minimum.Area && rule.MinSideMetres == minimum.Side, "prototype CABA rule " + rule.NamePattern + " equals the skill pack minimums",
                known ? R(minimum.Area) + "/" + R(minimum.Side) + " vs " + R(rule.MinAreaSquareMetres ?? -1) + "/" + R(rule.MinSideMetres ?? -1) : "pattern not in pack");
        }

        // ---- bounds: typed refusals before any planning ----
        Check(PrototypeGenerator.MinAlternatives == 1 && PrototypeGenerator.MaxAlternatives == 32, "prototype alternatives are bounded to 1..32");
        Check(PrototypeGenerator.MinLotSideMetres == 3 && PrototypeGenerator.MaxLotSideMetres == 100, "prototype lot sides are bounded to 3..100 m");
        foreach (var count in new[] { 0, -1, 33, int.MaxValue, int.MinValue })
        {
            var timer = Stopwatch.StartNew();
            var refused = PrototypeGenerator.Generate(new PrototypeRequest(ApartmentTypology.TwoBedroom, lot, count));
            timer.Stop();
            Check(refused.Status == PrototypeStatus.Refused && refused.Alternatives is null && refused.Refusals.Any(r => r.Code == PrototypeCodes.CountInvalid && r.Detail.Length > 0)
                && timer.Elapsed.TotalSeconds < 1, "prototype count " + Text(count) + " is refused without planning", Describe(refused));
        }
        foreach (var (label, badLot) in new[]
        {
            ("2 m wide", new PrototypeLot(2, 20)), ("zero deep", new PrototypeLot(8.66, 0)), ("NaN", new PrototypeLot(double.NaN, 20)),
            ("infinite", new PrototypeLot(8.66, double.PositiveInfinity)), ("101 m deep", new PrototypeLot(8.66, 101)), ("negative", new PrototypeLot(-8, 20)),
        })
        {
            var refused = PrototypeGenerator.Generate(new PrototypeRequest(ApartmentTypology.TwoBedroom, badLot, 2));
            Check(refused.Status == PrototypeStatus.Refused && refused.Alternatives is null && refused.Refusals.Any(r => r.Code == PrototypeCodes.LotInvalid),
                "a " + label + " lot is refused as prototype_lot_invalid", Describe(refused));
            Check(PrototypeSerializer.Serialize(refused).Length > 0, "a refused " + label + " lot still serializes");
        }
        var badTypology = PrototypeGenerator.Generate(new PrototypeRequest((ApartmentTypology)7, lot, 2));
        Check(badTypology.Status == PrototypeStatus.Refused && badTypology.Refusals.Any(r => r.Code == PrototypeCodes.TypologyInvalid), "an undefined typology is refused", Describe(badTypology));
        var badDoor = PrototypeGenerator.Generate(new PrototypeRequest(ApartmentTypology.TwoBedroom, lot, 2, DoorWidthMetres: 0));
        Check(badDoor.Status == PrototypeStatus.Refused && badDoor.Refusals.Any(r => r.Code == PrototypeCodes.OptionsInvalid), "a zero door width is refused", Describe(badDoor));
        var badWeights = PrototypeGenerator.Generate(new PrototypeRequest(ApartmentTypology.TwoBedroom, lot, 2, Weights: new ScoringWeights(0, 0, 0, 0, 0, 0)));
        Check(badWeights.Status == PrototypeStatus.Refused && badWeights.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid), "all-zero weights are refused", Describe(badWeights));
        var tooManyWalls = Enumerable.Range(0, PrototypeGenerator.MaxFixedWalls + 1).Select(i => new FixedWall("w" + Text(i), 0, 0, 1, 0)).ToArray();
        var wallFlood = PrototypeGenerator.Generate(new PrototypeRequest(ApartmentTypology.TwoBedroom, lot, 2, FixedWalls: tooManyWalls));
        Check(PrototypeGenerator.MaxFixedWalls == 64 && wallFlood.Status == PrototypeStatus.Refused && wallFlood.Refusals.Any(r => r.Code == PrototypeCodes.FixedWallInvalid),
            "more than 64 fixed walls are refused", Describe(wallFlood));
        var nullThrows = false;
        try { PrototypeGenerator.Generate(null!); } catch (ArgumentNullException) { nullThrows = true; }
        Check(nullThrows, "a null prototype request is a programming error (ArgumentNullException)");

        // ---- free generation: ranked, CABA-compliant layouts for each typology ----
        var results = new Dictionary<ApartmentTypology, PrototypeResult>();
        foreach (var (typology, bedrooms) in new[] { (ApartmentTypology.OneBedroom, 1), (ApartmentTypology.TwoBedroom, 2), (ApartmentTypology.ThreeBedroom, 3) })
        {
            var label = Bedrooms(bedrooms);
            var watch = Stopwatch.StartNew();
            var result = PrototypeGenerator.Generate(new PrototypeRequest(typology, lot, 6, Seed: 0));
            watch.Stop();
            results[typology] = result;
            Check(result.Status == PrototypeStatus.Ranked && result.Brief is not null && result.Alternatives is { } set && set.Ranked.Count >= 1,
                label + " prototype on an 8.66 x 20 m lot yields ranked layouts", Describe(result));
            Check(watch.Elapsed.TotalSeconds < 30, label + " prototype with 6 alternatives takes under 30 s", watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            if (result.Brief is not { } brief || result.Alternatives is not { } alternatives) continue;
            Check(alternatives.Alternatives.Count == 6 && result.Candidates.Count == 6 && result.Candidates.All(c => c.Strategy == PrototypeStrategies.WholeLot)
                && result.Candidates.Select(c => c.Seed).SequenceEqual(Enumerable.Range(0, 6)), label + " prototype without approvals plans 6 whole-lot candidates with seeds 0..5", Describe(result));
            Check(RankOrderProblem(alternatives) is null, label + " prototype ranks 1..k by total with the index tie-break", RankOrderProblem(alternatives) ?? "");
            foreach (var ranked in alternatives.Ranked)
            {
                var problem = Invariants(brief, ranked.Plan, lot, caba, Array.Empty<FixedRoom>());
                Check(problem is null, label + " ranked alternative " + Text(ranked.Index) + " honours brief, CABA minimums, lot and adjacency", problem ?? "");
            }
            Check(alternatives.Ranked.All(a => a.Plan.Rooms.Count(r => r.RoomId.StartsWith("bedroom_", StringComparison.Ordinal)) == bedrooms), label + " ranked layouts hold " + Text(bedrooms) + " bedrooms");
        }

        // ---- determinism: same input and seed give byte-identical JSON, whatever the culture ----
        var request = new PrototypeRequest(ApartmentTypology.TwoBedroom, lot, 6, Seed: 0);
        var first = PrototypeSerializer.Serialize(PrototypeGenerator.Generate(request));
        var second = PrototypeSerializer.Serialize(PrototypeGenerator.Generate(request));
        string thirdCulture;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            thirdCulture = PrototypeSerializer.Serialize(PrototypeGenerator.Generate(request));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(first == second && first == thirdCulture, "same prototype input and seed give byte-identical JSON across runs and cultures", first.Length.ToString(CultureInfo.InvariantCulture));
        Check(first == PrototypeSerializer.Serialize(results[ApartmentTypology.TwoBedroom]), "prototype JSON does not depend on earlier generations");
        using (var document = JsonDocument.Parse(first))
        {
            var json = document.RootElement;
            Check(json.GetProperty("prototype_version").GetInt32() == PrototypeResult.CurrentVersion && json.GetProperty("status").GetString() == "ranked"
                && json.GetProperty("typology").GetString() == "2br" && json.GetProperty("seed").GetInt32() == 0 && json.GetProperty("requested").GetInt32() == 6
                && json.GetProperty("lot").GetProperty("width_m").GetDouble() == 8.66 && json.GetProperty("lot").GetProperty("depth_m").GetDouble() == 20,
                "prototype JSON states version, status, typology, seed, count and lot", first.Length > 600 ? first[..600] : first);
            Check(json.GetProperty("brief").GetProperty("brief_version").GetInt32() == 1 && json.GetProperty("alternatives").GetProperty("alternatives_version").GetInt32() == 1
                && json.GetProperty("candidates").GetArrayLength() == 6 && json.GetProperty("fixed_rooms").GetArrayLength() == 0 && json.GetProperty("refusals").GetArrayLength() == 0,
                "prototype JSON embeds the brief, the ranked alternative set and the candidates");
            Check(BriefSerializer.Serialize(BriefParser.Parse(json.GetProperty("brief").GetRawText())) == json.GetProperty("brief").GetRawText(),
                "the embedded brief is the canonical brief JSON");
        }
        Check(PrototypeSerializer.Serialize(PrototypeGenerator.Generate(request with { Seed = 1 })) == PrototypeSerializer.Serialize(PrototypeGenerator.Generate(request with { Seed = 1 })),
            "another seed is deterministic too");

        // ---- approved invariants: fixed rooms keep their exact rectangle when reconfigured ----
        if (results[ApartmentTypology.TwoBedroom].Alternatives is not { } baseSet || baseSet.Ranked.Count == 0) { Check(false, "2-bedroom baseline has a ranked layout to approve"); return; }
        var best = baseSet.Ranked[0].Plan;
        FixedRoom Approve(string roomId) => new(roomId, best.Rooms.Single(r => r.RoomId == roomId).Rect);
        var approved = new[] { Approve("bedroom_1"), Approve("bathroom") };

        // Everything approved, nothing changed: the approved layout is the only ranked one.
        var all = best.Rooms.Select(r => new FixedRoom(r.RoomId, r.Rect)).ToArray();
        var replay = PrototypeGenerator.Generate(request with { FixedRooms = all });
        Check(replay.Status == PrototypeStatus.Ranked && replay.Alternatives is { } replaySet && replaySet.Ranked.Count == 1 && Geometry(replaySet.Ranked[0].Plan) == Geometry(best),
            "approving every room of the best layout and replanning keeps exactly that layout", Describe(replay));
        Check(replay.Alternatives is { } replayed && replayed.Alternatives.Where(a => a.Status == AlternativeStatus.Infeasible)
            .All(a => a.Reasons.Count > 0 && a.Reasons.All(reason => reason.Length > 0)), "every rejected replay alternative carries a reason", Describe(replay));

        foreach (var (label, reconfigured) in new[]
        {
            ("lot depth 20 -> 24 m", request with { Lot = new PrototypeLot(8.66, 24), FixedRooms = approved }),
            ("typology 2 -> 3 bedrooms", request with { Typology = ApartmentTypology.ThreeBedroom, FixedRooms = approved }),
            ("typology 2 -> 3 bedrooms and lot depth 20 -> 24 m", request with { Typology = ApartmentTypology.ThreeBedroom, Lot = new PrototypeLot(8.66, 24), FixedRooms = approved }),
        })
        {
            var result = PrototypeGenerator.Generate(reconfigured);
            Check(result.Status == PrototypeStatus.Ranked && result.Alternatives is { } set && set.Ranked.Count >= 1, label + " with approved bedroom and bathroom yields ranked layouts", Describe(result));
            if (result.Brief is not { } brief || result.Alternatives is not { } alternatives) continue;
            Check(result.Candidates.Count == 12 && result.Candidates.Count(c => c.Strategy == PrototypeStrategies.WholeLot) == 6
                && result.Candidates.Count(c => c.Strategy == PrototypeStrategies.AroundFixed) == 6 && alternatives.Alternatives.Count == 12,
                label + " plans 6 whole-lot and 6 around-approval candidates", Describe(result));
            foreach (var ranked in alternatives.Ranked)
            {
                Check(approved.All(f => ranked.Plan.Rooms.Single(r => r.RoomId == f.RoomId).Rect == f.Rect), label + " ranked alternative " + Text(ranked.Index) + " keeps the approved rectangles exactly",
                    Geometry(ranked.Plan));
                var problem = Invariants(brief, ranked.Plan, reconfigured.Lot, caba, approved);
                Check(problem is null, label + " ranked alternative " + Text(ranked.Index) + " honours brief, CABA minimums, lot, adjacency and approvals", problem ?? "");
            }
            Check(alternatives.Alternatives.Where(a => a.Status == AlternativeStatus.Infeasible).All(a => a.Reasons.Count > 0), label + " rejected alternatives carry reasons", Describe(result));
            // A free replan of the whole lot moves approved rooms; such layouts are rejected, naming the room.
            Check(alternatives.Alternatives.Any(a => a.Status == AlternativeStatus.Infeasible && result.Candidates[a.Index].Strategy == PrototypeStrategies.WholeLot
                && a.Reasons.Any(reason => reason.StartsWith(PrototypeCodes.FixedRoomMoved + "@", StringComparison.Ordinal))), label + " rejects whole-lot layouts that move an approved room, with the reason",
                Describe(result));
            Check(RankOrderProblem(alternatives) is null, label + " ranks 1..k by total with the index tie-break", RankOrderProblem(alternatives) ?? "");
            Check(PrototypeSerializer.Serialize(result) == PrototypeSerializer.Serialize(PrototypeGenerator.Generate(reconfigured)), label + " is deterministic");
            using var document = JsonDocument.Parse(PrototypeSerializer.Serialize(result));
            Check(document.RootElement.GetProperty("fixed_rooms").GetArrayLength() == 2 && document.RootElement.GetProperty("fixed_rooms")[0].GetProperty("room").GetString() == "bedroom_1",
                label + " JSON lists the approved rooms");
        }

        // ---- approvals that cannot hold are refused, naming the approval ----
        var maxTop = approved.Max(f => f.Rect.Top);
        var shallow = PrototypeGenerator.Generate(request with { Lot = new PrototypeLot(8.66, maxTop - 0.25), FixedRooms = approved });
        Check(shallow.Status == PrototypeStatus.Refused && shallow.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomOutsideLot), "a lot too shallow for an approved room is refused", Describe(shallow));
        var withSecond = new[] { Approve("bedroom_1"), Approve("bedroom_2") };
        var oneBedroom = PrototypeGenerator.Generate(request with { Typology = ApartmentTypology.OneBedroom, FixedRooms = withSecond });
        Check(oneBedroom.Status == PrototypeStatus.Refused && oneBedroom.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomUnknown && r.Subject.Contains("bedroom_2", StringComparison.Ordinal)),
            "approving bedroom_2 and switching to 1 bedroom is refused as fixed_room_unknown", Describe(oneBedroom));
        var overlap = PrototypeGenerator.Generate(request with { FixedRooms = new[] { new FixedRoom("bedroom_1", new PlanRect(0, 0, 3, 4)), new FixedRoom("bedroom_2", new PlanRect(2, 1, 3, 3)) } });
        Check(overlap.Status == PrototypeStatus.Refused && overlap.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomOverlap), "overlapping approved rooms are refused", Describe(overlap));
        var duplicate = PrototypeGenerator.Generate(request with { FixedRooms = new[] { new FixedRoom("bedroom_1", new PlanRect(0, 0, 3, 4)), new FixedRoom("bedroom_1", new PlanRect(4, 0, 3, 4)) } });
        Check(duplicate.Status == PrototypeStatus.Refused && duplicate.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomDuplicate), "approving one room twice is refused", Describe(duplicate));
        // 2 x 4.5 m: 9 m2 meets the brief minimum of the second bedroom but the side is below the CABA 2.5 m bedroom minimum.
        var narrow = PrototypeGenerator.Generate(request with { FixedRooms = new[] { new FixedRoom("bedroom_2", new PlanRect(0, 0, 2, 4.5)) } });
        Check(narrow.Status == PrototypeStatus.Refused && narrow.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomNoncompliant && r.Subject.Contains("bedroom_2", StringComparison.Ordinal)),
            "an approved bedroom below the CABA minimum side is refused", Describe(narrow));
        var offGrid = PrototypeGenerator.Generate(request with { FixedRooms = new[] { new FixedRoom("bedroom_2", new PlanRect(0.1, 0, 3, 3.5)) } });
        Check(offGrid.Status == PrototypeStatus.Refused && offGrid.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomInvalid), "an approved rectangle off the 1/8192 m grid is refused", Describe(offGrid));
        var nonFinite = PrototypeGenerator.Generate(request with { FixedRooms = new[] { new FixedRoom("bedroom_2", new PlanRect(0, 0, double.NaN, 3.5)) } });
        Check(nonFinite.Status == PrototypeStatus.Refused && nonFinite.Refusals.Any(r => r.Code == PrototypeCodes.FixedRoomInvalid), "a non-finite approved rectangle is refused", Describe(nonFinite));
        Check(PrototypeSerializer.Serialize(nonFinite).Length > 0, "a refused non-finite approval still serializes");

        // ---- fixed walls ----
        var bed = approved[0].Rect;
        var edgeWall = new FixedWall("bed_south", bed.X, bed.Y, bed.Right, bed.Y);
        var walled = PrototypeGenerator.Generate(request with { Typology = ApartmentTypology.ThreeBedroom, FixedRooms = approved, FixedWalls = new[] { edgeWall } });
        Check(walled.Status == PrototypeStatus.Ranked && walled.Alternatives is { } walledSet && walledSet.Ranked.Count >= 1, "an approved wall on the approved bedroom edge keeps ranked layouts", Describe(walled));
        Check(walled.Alternatives is { } kept && kept.Ranked.All(a => Covered(a.Plan, edgeWall)), "every ranked layout keeps the approved wall on a room edge", Describe(walled));
        // No layout puts room edges along 8 m of a line 0.5 m from the lot front; every candidate is rejected with the wall's reason.
        var strayWall = new FixedWall("stray", 0, 0.5, 8, 0.5);
        var stray = PrototypeGenerator.Generate(request with { FixedWalls = new[] { strayWall } });
        Check(stray.Status == PrototypeStatus.Infeasible && stray.Alternatives is { } straySet && straySet.Ranked.Count == 0
            && straySet.Alternatives.Any(a => a.Reasons.Any(reason => reason.StartsWith(PrototypeCodes.FixedWallMissing + "@stray", StringComparison.Ordinal))),
            "an approved wall no layout can keep rejects every candidate, with fixed_wall_missing", Describe(stray));
        foreach (var (label, wall) in new[]
        {
            ("diagonal", new FixedWall("d", 0, 0, 1, 1)), ("zero-length", new FixedWall("z", 1, 1, 1, 1)), ("outside the lot", new FixedWall("o", 0, 21, 2, 21)),
            ("off-grid", new FixedWall("g", 0.1, 0, 0.1, 2)), ("unnamed", new FixedWall(" ", 0, 0, 1, 0)),
        })
        {
            var refused = PrototypeGenerator.Generate(request with { FixedWalls = new[] { wall } });
            Check(refused.Status == PrototypeStatus.Refused && refused.Refusals.Any(r => r.Code == PrototypeCodes.FixedWallInvalid), "a " + label + " approved wall is refused", Describe(refused));
        }
    }

    // CABA minimum area and side by room-name pattern, from the pack's min rules on area_m2 and min_side_m.
    static Dictionary<string, (double Area, double Side)> CabaMinimums(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var areas = new Dictionary<string, double>(StringComparer.Ordinal);
        var sides = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var rule in document.RootElement.GetProperty("rules").EnumerateArray())
        {
            if (!rule.TryGetProperty("target", out var target) || !target.TryGetProperty("room", out var room) || !rule.TryGetProperty("check", out var check)) continue;
            if (check.GetProperty("op").GetString() != "min" || !check.TryGetProperty("value", out var value)) continue;
            var property = check.GetProperty("property").GetString();
            if (property == "area_m2") areas[room.GetString()!] = value.GetDouble();
            if (property == "min_side_m") sides[room.GetString()!] = value.GetDouble();
        }
        return areas.Keys.Where(sides.ContainsKey).ToDictionary(key => key, key => (areas[key], sides[key]), StringComparer.Ordinal);
    }

    // Case-insensitive '*' suffix glob, the pack's own matching for these patterns.
    static bool NameMatches(string pattern, string name) =>
        pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    // Independent oracle for a ranked prototype layout: one room per brief room; area within the brief range and at
    // least every matching CABA minimum area (and that side squared); short side at least every matching CABA minimum
    // side; inside the lot; no overlap; required adjacencies share a door-width wall, forbidden ones no edge; area
    // budget; approved rooms bit-identical. Null when everything holds.
    static string? Invariants(ProjectBrief brief, SpacePlan plan, PrototypeLot lot, Dictionary<string, (double Area, double Side)> caba, IReadOnlyList<FixedRoom> approved)
    {
        if (!plan.IsFeasible) return "not feasible";
        var problems = new List<string>();
        if (plan.Rooms.Count != brief.Rooms.Count) problems.Add("rooms " + Text(plan.Rooms.Count) + " != " + Text(brief.Rooms.Count));
        foreach (var spec in brief.Rooms)
        {
            var placed = plan.Rooms.Where(r => r.RoomId == spec.Id).ToArray();
            if (placed.Length != 1) { problems.Add(spec.Id + " placed " + Text(placed.Length) + " times"); continue; }
            var rect = placed[0].Rect;
            var area = rect.Area;
            var shortSide = Math.Min(rect.Width, rect.Depth);
            var minArea = spec.MinAreaSquareMetres?.Value ?? 0;
            double minSide = 0;
            foreach (var (pattern, minimum) in caba)
                if (NameMatches(pattern, spec.Name)) { minArea = Math.Max(minArea, Math.Max(minimum.Area, minimum.Side * minimum.Side)); minSide = Math.Max(minSide, minimum.Side); }
            if (area < minArea) problems.Add(spec.Id + " area " + R(area) + " < " + R(minArea));
            if (spec.MaxAreaSquareMetres is { } max && area > max.Value + Tolerance) problems.Add(spec.Id + " area " + R(area) + " > " + R(max.Value));
            if (shortSide < minSide) problems.Add(spec.Id + " short side " + R(shortSide) + " < CABA " + R(minSide));
            if (!(rect.X >= 0 && rect.Y >= 0 && rect.Right <= lot.WidthMetres && rect.Top <= lot.DepthMetres && rect.Width > 0 && rect.Depth > 0))
                problems.Add(spec.Id + " outside the lot");
        }
        for (var i = 0; i < plan.Rooms.Count; i++)
            for (var j = i + 1; j < plan.Rooms.Count; j++)
            {
                var a = plan.Rooms[i].Rect;
                var b = plan.Rooms[j].Rect;
                if (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > EdgeEpsilon && Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y) > EdgeEpsilon)
                    problems.Add(plan.Rooms[i].RoomId + " overlaps " + plan.Rooms[j].RoomId);
            }
        foreach (var adjacency in brief.Adjacencies)
        {
            var a = plan.Rooms.FirstOrDefault(r => r.RoomId == adjacency.A);
            var b = plan.Rooms.FirstOrDefault(r => r.RoomId == adjacency.B);
            if (a is null || b is null) continue;
            var shared = SharedEdge(a.Rect, b.Rect);
            if (adjacency.Kind == BriefAdjacencyKind.Required && shared < plan.DoorWidthMetres) problems.Add("required " + adjacency.A + "/" + adjacency.B + " shares " + R(shared));
            if (adjacency.Kind == BriefAdjacencyKind.Forbidden && shared > EdgeEpsilon) problems.Add("forbidden " + adjacency.A + "/" + adjacency.B + " shares " + R(shared));
        }
        if (brief.Constraints.TotalAreaBudgetSquareMetres is { } budget && plan.Rooms.Sum(r => r.AreaSquareMetres) > budget.Value + Tolerance) problems.Add("area budget exceeded");
        foreach (var fixedRoom in approved)
        {
            var placed = plan.Rooms.FirstOrDefault(r => r.RoomId == fixedRoom.RoomId);
            if (placed is null || BitConverter.DoubleToInt64Bits(placed.Rect.X) != BitConverter.DoubleToInt64Bits(fixedRoom.Rect.X)
                || BitConverter.DoubleToInt64Bits(placed.Rect.Y) != BitConverter.DoubleToInt64Bits(fixedRoom.Rect.Y)
                || BitConverter.DoubleToInt64Bits(placed.Rect.Width) != BitConverter.DoubleToInt64Bits(fixedRoom.Rect.Width)
                || BitConverter.DoubleToInt64Bits(placed.Rect.Depth) != BitConverter.DoubleToInt64Bits(fixedRoom.Rect.Depth))
                problems.Add("approved " + fixedRoom.RoomId + " moved");
        }
        return problems.Count == 0 ? null : string.Join("; ", problems.Take(8));
    }

    // True when the union of room edges on the wall's line covers the whole wall.
    static bool Covered(SpacePlan plan, FixedWall wall)
    {
        var horizontal = wall.Y0 == wall.Y1;
        var line = horizontal ? wall.Y0 : wall.X0;
        var from = horizontal ? Math.Min(wall.X0, wall.X1) : Math.Min(wall.Y0, wall.Y1);
        var to = horizontal ? Math.Max(wall.X0, wall.X1) : Math.Max(wall.Y0, wall.Y1);
        var spans = new List<(double Start, double End)>();
        foreach (var room in plan.Rooms)
        {
            var r = room.Rect;
            if (horizontal && (Math.Abs(r.Y - line) < EdgeEpsilon || Math.Abs(r.Top - line) < EdgeEpsilon)) spans.Add((r.X, r.Right));
            if (!horizontal && (Math.Abs(r.X - line) < EdgeEpsilon || Math.Abs(r.Right - line) < EdgeEpsilon)) spans.Add((r.Y, r.Top));
        }
        var reach = from;
        foreach (var (start, end) in spans.OrderBy(s => s.Start))
        {
            if (start > reach + EdgeEpsilon) break;
            reach = Math.Max(reach, end);
        }
        return reach >= to - EdgeEpsilon;
    }

    static double SharedEdge(PlanRect a, PlanRect b)
    {
        if (Math.Abs(a.Right - b.X) < EdgeEpsilon || Math.Abs(b.Right - a.X) < EdgeEpsilon) return Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        if (Math.Abs(a.Top - b.Y) < EdgeEpsilon || Math.Abs(b.Top - a.Y) < EdgeEpsilon) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        return 0;
    }

    static string? RankOrderProblem(AlternativeSet set)
    {
        var ranked = set.Alternatives.TakeWhile(a => a.Status == AlternativeStatus.Ranked).ToArray();
        if (ranked.Length != set.Ranked.Count) return "ranked alternatives are not listed first";
        for (var i = 0; i < ranked.Length; i++)
        {
            if (ranked[i].Rank != i + 1) return "rank " + Text(ranked[i].Rank) + " at position " + Text(i);
            if (i > 0 && (ranked[i].Total > ranked[i - 1].Total || (ranked[i].Total == ranked[i - 1].Total && ranked[i].Index < ranked[i - 1].Index)))
                return "order broken at position " + Text(i);
        }
        return set.Alternatives.Where(a => a.Status != AlternativeStatus.Ranked).All(a => a.Rank == 0) ? null : "unranked alternative with a rank";
    }

    static bool Pair(BriefAdjacency adjacency, string a, string b) => (adjacency.A == a && adjacency.B == b) || (adjacency.A == b && adjacency.B == a);

    static string Bedrooms(int count) => Text(count) + "-bedroom";

    static string Geometry(SpacePlan plan) => string.Join(";", plan.Rooms.Select(r => r.RoomId + ":" + R(r.Rect.X) + "," + R(r.Rect.Y) + "," + R(r.Rect.Width) + "," + R(r.Rect.Depth)));

    static string Describe(PrototypeResult result) => result.Status + " refusals[" + string.Join(" | ", result.Refusals.Select(r => r.Code + "@" + r.Subject)) + "] "
        + (result.Alternatives is { } set ? string.Join(" | ", set.Alternatives.Take(12).Select(a => Text(a.Index) + ":" + a.Status + " r" + Text(a.Rank) + " t" + R(a.Total) + " "
            + string.Join(",", a.Reasons.Take(2).Select(reason => reason.Length > 90 ? reason[..90] : reason)))) : "no set");

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
