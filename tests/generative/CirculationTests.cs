using System.Globalization;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-04 circulation oracle: doors between adjacent rooms (shared wall of at least the door width) and an entrance on
// the lot edge form a reachability graph; exact hand-computed route lengths on crafted plans (corridor, loop with a
// shorter and a longer route, ensuite bathroom, bedroom behind a bedroom, room behind a too-short wall, upper level),
// entrance selection (edges, explicit room, bedrooms excluded), typed refusals, an independent graph oracle over real
// planner and prototype layouts, the opt-in seventh ranking criterion with exact weights and totals, the hard
// circulation gate, backward compatibility with the six-criterion ranker, and canonical determinism across runs,
// room orders and cultures. Unit-tested evidence only.
static class CirculationTests
{
    const double Tolerance = 1e-9;
    static readonly string[] BaseCriteria = { "area_fit", "adjacency", "proportions", "compactness", "perimeter", "compliance" };

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "circulation: " + name, detail);
        var s2 = Math.Sqrt(4.5);

        // ---- corridor: hall | living | bedroom, entrance on the south side of the hall ----
        var corridorBrief = Lot("corridor", 9, 3, "hall", "living", "bedroom_a");
        var corridor = Plan(corridorBrief, 1, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("bedroom_a", 6, 0, 3, 3));
        var model = Analyze(corridorBrief, corridor);
        Check(model.Status == CirculationStatus.Valid && model.IsValid && model.Violations.Count == 0 && model.Checks.All(c => c.Hard && c.Satisfied && c.Detail.Length > 0),
            "corridor circulation is valid with every check satisfied", Describe(model));
        Check(model.Checks.Count(c => c.Code == CirculationCodes.Entrance && c.Subject == "plan") == 1 && model.Checks.Count(c => c.Code == CirculationCodes.Reachable) == 3
            && model.Checks.Count(c => c.Code == CirculationCodes.BedroomAccess) == 1 && model.Checks.Any(c => c.Code == CirculationCodes.BedroomAccess && c.Subject == "bedroom_a"),
            "checks: one entrance, one reachability per room, one bedroom access per bedroom", Describe(model));
        Check(model.Entrance is { Id: CirculationModel.EntranceDoorId, From: CirculationModel.Exterior, To: "hall", Level: 0 } entrance && model.EntranceEdge == LotEdge.South
            && Near(entrance.MidX, 1.5) && entrance.Y0 == 0 && entrance.Y1 == 0 && Near(Length(entrance), 0.9) && entrance.Used,
            "the entrance is a 0.9 m door centred on the hall's south side on the lot edge", Describe(model));
        Check(model.DoorWidthMetres == 0.9 && model.BriefName == "corridor" && model.Seed == 1, "the model carries the plan's door width, brief name and seed");
        Check(Pairs(model) == "bedroom_a|living;hall|living" && model.Doors.All(d => d.Used && d.Level == 0 && Near(Length(d), 0.9)),
            "corridor doors connect exactly the two adjacent pairs", Describe(model));
        var hallLiving = model.Doors.FirstOrDefault(d => d.From == "hall" && d.To == "living");
        Check(hallLiving is not null && hallLiving.X0 == 3 && hallLiving.X1 == 3 && Near(hallLiving.MidY, 1.5), "a door is centred on the shared wall", Describe(model));
        Check(model.Routes.Select(r => r.Instance).SequenceEqual(new[] { "bedroom_a", "hall", "living" }), "routes are ordered by instance id");
        Check(Near(Route(model, "hall")?.LengthMetres, 1.5) && Near(Route(model, "living")?.LengthMetres, 1.5 + s2) && Near(Route(model, "bedroom_a")?.LengthMetres, 4.5 + s2),
            "corridor route lengths: 1.5, 1.5 + sqrt(4.5), 4.5 + sqrt(4.5) m", Describe(model));
        Check(Route(model, "bedroom_a") is { Reachable: true, AvoidsBedrooms: true, IsBedroom: true } bedroute && bedroute.Rooms.SequenceEqual(new[] { "hall", "living", "bedroom_a" })
            && bedroute.Doors.Count == 3 && bedroute.Doors[0] == CirculationModel.EntranceDoorId,
            "the bedroom route enters hall, living, bedroom through three doors", Describe(model));
        Check(Route(model, "hall") is { IsBedroom: false, AvoidsBedrooms: true } hallRoute && hallRoute.Rooms.SequenceEqual(new[] { "hall" }) && hallRoute.Doors.SequenceEqual(new[] { "entrance" }),
            "the entrance room route crosses only the entrance door");
        Check(Oracle(corridorBrief, corridor, model, 0.9, true) is null, "corridor model agrees with the independent graph oracle", Oracle(corridorBrief, corridor, model, 0.9, true) ?? "");

        // ---- a loop: the shorter of two routes is taken; an unused door is reported ----
        var loopBrief = Lot("loop", 6, 6, "hall", "living", "kitchen", "bedroom_a");
        var loop = Plan(loopBrief, 2, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("kitchen", 0, 3, 3, 3), P("bedroom_a", 3, 3, 3, 3));
        var loopModel = Analyze(loopBrief, loop);
        Check(loopModel.IsValid && Pairs(loopModel) == "bedroom_a|kitchen;bedroom_a|living;hall|kitchen;hall|living", "a 2 x 2 grid has four doors and none across a corner", Describe(loopModel));
        Check(Route(loopModel, "bedroom_a") is { } loopBed && loopBed.Rooms.SequenceEqual(new[] { "hall", "living", "bedroom_a" }) && Near(loopBed.LengthMetres, 2 * s2 + 1.5),
            "the bedroom is reached by the shorter route through the living room (2 sqrt(4.5) + 1.5 m, not 4.5 + sqrt(4.5) m)", Describe(loopModel));
        Check(Near(Route(loopModel, "kitchen")?.LengthMetres, 4.5), "the kitchen straight above the hall is 4.5 m away", Describe(loopModel));
        Check(loopModel.Doors.Single(d => d.From == "bedroom_a" && d.To == "kitchen").Used == false && loopModel.Doors.Where(d => !(d.From == "bedroom_a" && d.To == "kitchen")).All(d => d.Used),
            "only the door no route crosses is reported unused", Describe(loopModel));
        Check(Oracle(loopBrief, loop, loopModel, 0.9, true) is null, "loop model agrees with the oracle", Oracle(loopBrief, loop, loopModel, 0.9, true) ?? "");

        // ---- a bedroom reachable only through another bedroom ----
        var chainBrief = Lot("chain", 12, 3, "hall", "living", "bedroom_a", "bedroom_b");
        var chain = Plan(chainBrief, 3, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("bedroom_a", 6, 0, 3, 3), P("bedroom_b", 9, 0, 3, 3));
        var chainModel = Analyze(chainBrief, chain);
        Check(chainModel.Status == CirculationStatus.Invalid && chainModel.Violations.Count == 1
            && chainModel.Violations[0] is { Code: CirculationCodes.BedroomAccess, Subject: "bedroom_b", Hard: true, Satisfied: false } && chainModel.Violations[0].Detail.Contains("bedroom_a", StringComparison.Ordinal),
            "a bedroom behind another bedroom fails circulation_bedroom_access naming the bedroom passed", Describe(chainModel));
        Check(Route(chainModel, "bedroom_b") is { Reachable: true, AvoidsBedrooms: false } chainRoute && chainRoute.Rooms.SequenceEqual(new[] { "hall", "living", "bedroom_a", "bedroom_b" })
            && Near(chainRoute.LengthMetres, 7.5 + s2) && chainModel.Checks.Any(c => c.Code == CirculationCodes.Reachable && c.Subject == "bedroom_b" && c.Satisfied),
            "it is still reachable (7.5 + sqrt(4.5) m), only not without passing a bedroom", Describe(chainModel));
        Check(Oracle(chainBrief, chain, chainModel, 0.9, true) is null, "bedroom chain agrees with the oracle", Oracle(chainBrief, chain, chainModel, 0.9, true) ?? "");

        // ---- an ensuite bathroom behind a bedroom is fine ----
        var ensuiteBrief = Lot("ensuite", 8, 3, "hall", "bedroom_a", "bath");
        var ensuite = Plan(ensuiteBrief, 4, P("hall", 0, 0, 3, 3), P("bedroom_a", 3, 0, 3, 3), P("bath", 6, 0, 2, 3));
        var ensuiteModel = Analyze(ensuiteBrief, ensuite);
        Check(ensuiteModel.IsValid && Route(ensuiteModel, "bath") is { Reachable: true, AvoidsBedrooms: false, IsBedroom: false } && Route(ensuiteModel, "bedroom_a") is { AvoidsBedrooms: true },
            "a non-bedroom reachable only through a bedroom is valid (only bedrooms need a bedroom-free route)", Describe(ensuiteModel));
        Check(Near(Route(ensuiteModel, "bath")?.LengthMetres, s2 + 3 + 1), "the ensuite route length is sqrt(4.5) + 3 + 1 m", Describe(ensuiteModel));

        // ---- a room behind a wall shorter than the door is unreachable ----
        var storeBrief = Lot("store", 9, 3, "hall", "living", "store");
        var store = Plan(storeBrief, 5, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("store", 6, 2.5, 3, 0.5));
        var storeModel = Analyze(storeBrief, store);
        Check(storeModel.Status == CirculationStatus.Invalid && Pairs(storeModel) == "hall|living" && Route(storeModel, "store") is { Reachable: false, AvoidsBedrooms: false } storeRoute
            && !double.IsFinite(storeRoute.LengthMetres) && storeRoute.Rooms.Count == 0 && storeRoute.Doors.Count == 0
            && storeModel.Violations.Count == 2 && storeModel.Violations[0] is { Code: CirculationCodes.Reachable, Subject: "store" }
            && storeModel.Violations[1] is { Code: CirculationCodes.Passage, Subject: "store" },
            "a room sharing only 0.5 m of wall gets no door and fails circulation_reachable", Describe(storeModel));
        Check(Oracle(storeBrief, store, storeModel, 0.9, true) is null, "unreachable room agrees with the oracle", Oracle(storeBrief, store, storeModel, 0.9, true) ?? "");
        var json = CirculationSerializer.Serialize(storeModel);
        using (var document = JsonDocument.Parse(json))
        {
            var routes = document.RootElement.GetProperty("routes");
            var storeJson = routes.EnumerateArray().First(r => r.GetProperty("instance").GetString() == "store");
            Check(storeJson.GetProperty("length_m").ValueKind == JsonValueKind.Null && storeJson.GetProperty("reachable").GetBoolean() == false,
                "an unreachable route serializes its non-finite length as null", json);
            Check(json.StartsWith("{\"circulation_version\":1,\"status\":\"invalid\",", StringComparison.Ordinal) && !json.Contains('\n', StringComparison.Ordinal),
                "canonical circulation JSON is compact with version and status first", json);
        }

        // ---- upper levels: no stairs are modelled ----
        var twoLevelBrief = Lot("two levels", 6, 3, "hall", "living", "upper");
        var twoLevel = new SpacePlan(SpacePlanStatus.Feasible, "two levels", 6, 2, new PlanFootprint(6, 3), 0.9,
            new[] { P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("upper", 0, 0, 3, 3, level: 1) }, Array.Empty<PlanConstraint>(), 0);
        var twoLevelModel = Analyze(twoLevelBrief, twoLevel);
        Check(twoLevelModel.Status == CirculationStatus.Valid && Route(twoLevelModel, "upper") is { Reachable: false, AvoidsBedrooms: false }
            && twoLevelModel.Checks.Count(c => c.Code == CirculationCodes.Unsupported) == 1
            && twoLevelModel.Checks.Any(c => c is { Code: CirculationCodes.Unsupported, Subject: "upper", Hard: false, Satisfied: false } && c.Detail.Contains("stair", StringComparison.Ordinal))
            && !twoLevelModel.Checks.Any(c => c.Subject == "upper" && c.Code != CirculationCodes.Unsupported) && Pairs(twoLevelModel) == "hall|living",
            "a room on an upper level is a soft circulation_unsupported outcome (no stairs are modelled), not a plan defect", Describe(twoLevelModel));
        Check(Oracle(twoLevelBrief, twoLevel, twoLevelModel, 0.9, true) is null, "two-level model agrees with the oracle", Oracle(twoLevelBrief, twoLevel, twoLevelModel, 0.9, true) ?? "");

        // ---- a dry room behind a bedroom fails; only wet rooms may sit behind one ----
        var behindBrief = Lot("behind", 9, 3, "hall", "bedroom_a", "kitchen");
        var behind = Plan(behindBrief, 7, P("hall", 0, 0, 3, 3), P("bedroom_a", 3, 0, 3, 3), P("kitchen", 6, 0, 3, 3));
        var behindModel = Analyze(behindBrief, behind);
        Check(behindModel.Status == CirculationStatus.Invalid && behindModel.Violations.Count == 1
            && behindModel.Violations[0] is { Code: CirculationCodes.Passage, Subject: "kitchen", Hard: true } && behindModel.Violations[0].Detail.Contains("bedroom_a", StringComparison.Ordinal)
            && Route(behindModel, "kitchen") is { Reachable: true, AvoidsBedrooms: false },
            "a kitchen reachable only through a bedroom fails circulation_passage", Describe(behindModel));
        var livingBehind = Plan(behindBrief, 8, P("hall", 0, 0, 3, 3), P("bedroom_a", 3, 0, 3, 3), new PlannedRoom("living", "living", "Living room", 1, 0, new PlanRect(6, 0, 3, 3)));
        var livingBehindModel = Analyze(behindBrief, livingBehind);
        Check(livingBehindModel.Status == CirculationStatus.Invalid && livingBehindModel.Violations.Any(c => c.Code == CirculationCodes.Passage && c.Subject == "living"),
            "a living room behind a bedroom fails circulation_passage too", Describe(livingBehindModel));
        Check(Oracle(behindBrief, behind, behindModel, 0.9, true) is null, "dry room behind a bedroom agrees with the oracle", Oracle(behindBrief, behind, behindModel, 0.9, true) ?? "");
        foreach (var wet in new[] { "bath", "ensuite", "Vestidor", "walk-in closet", "WC", "dressing" })
        {
            var wetModel = Analyze(behindBrief, Plan(behindBrief, 9, P("hall", 0, 0, 3, 3), P("bedroom_a", 3, 0, 3, 3), new PlannedRoom("w", "w", wet, 1, 0, new PlanRect(6, 0, 3, 3))));
            Check(wetModel.IsValid && !wetModel.Checks.Any(c => c.Subject == "w" && c.Code == CirculationCodes.Passage), "a '" + wet + "' behind a bedroom is a valid pass-through", Describe(wetModel));
        }

        // ---- independent minimality: symmetric tie, and more doors but shorter ----
        var tieBrief = Lot("tie", 6, 6, "hall", "study_w", "study_e", "bedroom_a");
        var tie = Plan(tieBrief, 13, P("hall", 0, 0, 6, 2), P("study_w", 0, 2, 3, 2), P("study_e", 3, 2, 3, 2), P("bedroom_a", 0, 4, 6, 2));
        var tieModel = Analyze(tieBrief, tie);
        Check(tieModel.IsValid && Route(tieModel, "bedroom_a") is { } tieRoute && tieRoute.Rooms.SequenceEqual(new[] { "hall", "study_e", "bedroom_a" })
            && Near(tieRoute.LengthMetres, 4.5 + Math.Sqrt(3.25)),
            "of two equal routes the one through the door listed first (door:bedroom_a/study_e) wins the tie", Describe(tieModel));
        Check(CirculationSerializer.Serialize(Analyze(tieBrief, tie with { Rooms = tie.Rooms.Reverse().ToArray() })) == CirculationSerializer.Serialize(tieModel),
            "the tie-break does not depend on room order");
        Check(Oracle(tieBrief, tie, tieModel, 0.9, true) is null, "tie model agrees with the oracle", Oracle(tieBrief, tie, tieModel, 0.9, true) ?? "");
        var detourBrief = Lot("detour", 8, 4, "hall", "gallery", "den", "study", "bedroom_a");
        var detour = Plan(detourBrief, 14, P("hall", 0, 0, 2, 4), P("gallery", 2, 0, 4, 1), P("den", 2, 1, 2, 3), P("study", 4, 1, 2, 3), P("bedroom_a", 6, 0, 2, 4));
        var detourModel = Analyze(detourBrief, detour, new CirculationOptions(EntranceEdges: new[] { LotEdge.North }));
        Check(detourModel.IsValid && Route(detourModel, "bedroom_a") is { } detourRoute && detourRoute.Rooms.SequenceEqual(new[] { "hall", "den", "study", "bedroom_a" })
            && detourRoute.Doors.Count == 4 && Near(detourRoute.LengthMetres, Math.Sqrt(3.25) + 4 + Math.Sqrt(1.25)),
            "a route with more doors (4, sqrt(3.25) + 4 + sqrt(1.25) m) beats the 3-door route through the gallery", Describe(detourModel));
        Check(Oracle(detourBrief, detour, detourModel, 0.9, true) is null, "detour model agrees with the oracle", Oracle(detourBrief, detour, detourModel, 0.9, true) ?? "");

        // ---- partial walls, tolerances, overlaps ----
        var partialBrief = Lot("partial", 6, 5, "hall", "living");
        var partialModel = Analyze(partialBrief, Plan(partialBrief, 15, P("hall", 0, 0, 3, 3), P("living", 3, 1, 3, 4)));
        Check(partialModel.Doors.Count == 1 && partialModel.Doors[0] is { X0: 3.0, X1: 3.0 } partialDoor && Near(partialDoor.MidY, 2) && Near(Length(partialDoor), 0.9),
            "a door is centred on the overlapping part of a partial wall (y 1..3 gives y = 2)", Describe(partialModel));
        var nearBrief = Lot("near", 7, 3, "hall", "living");
        var nearModel = Analyze(nearBrief, Plan(nearBrief, 16, P("hall", 0, 0, 3, 3), P("living", 3.00000001, 0, 3, 3)));
        Check(nearModel.Doors.Count == 1 && nearModel.IsValid, "edges 1e-8 m apart still coincide (1e-7 m tolerance)", Describe(nearModel));
        var gapModel = Analyze(nearBrief, Plan(nearBrief, 17, P("hall", 0, 0, 3, 3), P("living", 3.000001, 0, 3, 3)));
        Check(gapModel.Doors.Count == 0, "edges 1e-6 m apart do not coincide", Describe(gapModel));
        var tallBrief = Lot("tall", 7, 6, "hall", "living");
        var shortWallModel = Analyze(tallBrief, Plan(tallBrief, 18, P("hall", 0, 0, 3, 3), P("living", 3, 3 - (0.9 - 1e-8), 3, 3)));
        Check(shortWallModel.Doors.Count == 1, "a shared wall 1e-8 m shorter than the door still takes it", Describe(shortWallModel));
        var overlapModel = Analyze(nearBrief, Plan(nearBrief, 19, P("hall", 0, 0, 3, 3), P("living", 2, 0, 3, 3)));
        Check(overlapModel.Status == CirculationStatus.Refused
            && overlapModel.Checks.Any(c => c.Code == CirculationCodes.PlanInvalid && c.Subject == "hall/living" && c.Detail.Contains("overlap", StringComparison.Ordinal)),
            "overlapping rooms are refused as circulation_plan_invalid naming the pair", Describe(overlapModel));
        var touchingModel = Analyze(nearBrief, Plan(nearBrief, 20, P("hall", 0, 0, 3, 3), P("living", 3 - 1e-8, 0, 3, 3)));
        Check(touchingModel.Status != CirculationStatus.Refused && touchingModel.Doors.Count == 1, "an overlap below the 1e-7 m tolerance is not refused", Describe(touchingModel));

        // ---- entrance: street edges and no wet room as fallback ----
        var wetFrontBrief = Lot("wet front", 6, 3, "bedroom_a", "bath", "kitchen");
        var wetFrontModel = Analyze(wetFrontBrief, Plan(wetFrontBrief, 21, P("bedroom_a", 0, 0, 3, 3), P("bath", 3, 0, 3, 1.5), P("kitchen", 3, 1.5, 3, 1.5)),
            new CirculationOptions(EntranceEdges: new[] { LotEdge.South }));
        Check(wetFrontModel.Entrance is null && wetFrontModel.Violations.Any(c => c.Code == CirculationCodes.Entrance),
            "a bathroom is never the fallback entrance: with only a bedroom and a bathroom on the south street edge there is no entrance", Describe(wetFrontModel));
        var streetPlans = new[]
        {
            Plan(corridorBrief, 30, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("bedroom_a", 6, 0, 3, 3)),
            Plan(corridorBrief, 31, P("bedroom_a", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("hall", 6, 0, 3, 3)),
        };
        var eastStreet = CirculationRanker.Rank(corridorBrief, streetPlans, new CirculationScoring(Options: new CirculationOptions(EntranceEdges: new[] { LotEdge.East })));
        Check(eastStreet.Ranked.Count == 1 && eastStreet.Ranked[0].Index == 1
            && eastStreet.Alternatives.First(a => a.Index == 0).Reasons.Any(r => r.StartsWith(CirculationCodes.Entrance + "@plan", StringComparison.Ordinal)),
            "with the street on the east edge only the plan with the hall there is ranked", Describe(eastStreet));

        // ---- entrance selection ----
        var west = Analyze(loopBrief, loop, new CirculationOptions(EntranceEdges: new[] { LotEdge.West }));
        Check(west.IsValid && west.EntranceEdge == LotEdge.West && west.Entrance is { To: "hall", X0: 0.0, X1: 0.0 } westDoor && Near(westDoor.MidY, 1.5) && Near(Route(west, "hall")?.LengthMetres, 1.5),
            "EntranceEdges [West] puts the entrance on the hall's west side", Describe(west));
        var northEast = Analyze(loopBrief, loop, new CirculationOptions(EntranceEdges: new[] { LotEdge.North, LotEdge.East }));
        Check(northEast.Entrance is { To: "kitchen" } neDoor && northEast.EntranceEdge == LotEdge.North && Near(neDoor.MidX, 1.5) && neDoor.Y0 == 6 && neDoor.Y1 == 6,
            "with the hall off the allowed edges, the largest non-bedroom room on them (kitchen before living by id) is the entrance", Describe(northEast));
        var bedroomOnly = Analyze(corridorBrief, corridor, new CirculationOptions(EntranceEdges: new[] { LotEdge.East }));
        Check(bedroomOnly.Status == CirculationStatus.Invalid && bedroomOnly.Entrance is null && bedroomOnly.EntranceEdge is null
            && bedroomOnly.Violations.Any(c => c.Code == CirculationCodes.Entrance && c.Subject == "plan") && bedroomOnly.Routes.All(r => !r.Reachable)
            && bedroomOnly.Violations.Count(c => c.Code == CirculationCodes.Reachable) == 3,
            "a bedroom is never chosen as entrance by default: only a bedroom on the east edge leaves no entrance", Describe(bedroomOnly));
        var viaLiving = Analyze(corridorBrief, corridor, new CirculationOptions(EntranceRoomId: "living"));
        Check(viaLiving.IsValid && viaLiving.Entrance is { To: "living" } livingDoor && Near(livingDoor.MidX, 4.5) && livingDoor.Y0 == 0
            && Near(Route(viaLiving, "hall")?.LengthMetres, s2 + 1.5) && Near(Route(viaLiving, "bedroom_a")?.LengthMetres, s2 + 1.5) && Near(Route(viaLiving, "living")?.LengthMetres, 1.5),
            "an explicit entrance room is used", Describe(viaLiving));
        var wideDoor = Analyze(corridorBrief, corridor, new CirculationOptions(DoorWidthMetres: 3));
        Check(wideDoor.IsValid && wideDoor.DoorWidthMetres == 3 && wideDoor.Doors.Count == 2 && wideDoor.Doors.All(d => Near(Length(d), 3)),
            "a door exactly as wide as the shared wall still fits", Describe(wideDoor));
        var tooWide = Analyze(corridorBrief, corridor, new CirculationOptions(DoorWidthMetres: 3.5));
        Check(tooWide.Status == CirculationStatus.Invalid && tooWide.Doors.Count == 0 && tooWide.Entrance is null, "a door wider than every wall leaves no doors and no entrance", Describe(tooWide));

        // ---- refusals ----
        void Refused(string name, CirculationModel refused, string code, string subject) =>
            Check(refused.Status == CirculationStatus.Refused && refused.Routes.Count == 0 && refused.Doors.Count == 0 && refused.Entrance is null
                && refused.Checks.Count > 0 && refused.Checks.All(c => !c.Satisfied) && refused.Checks.Any(c => c.Code == code && c.Subject == subject && c.Detail.Length > 0), name, Describe(refused));
        Refused("an unknown entrance room is refused", Analyze(corridorBrief, corridor, new CirculationOptions(EntranceRoomId: "ghost")), CirculationCodes.OptionsInvalid, "entrance_room");
        Refused("a zero door width is refused", Analyze(corridorBrief, corridor, new CirculationOptions(DoorWidthMetres: 0)), CirculationCodes.OptionsInvalid, "door_width_m");
        Refused("a NaN door width is refused", Analyze(corridorBrief, corridor, new CirculationOptions(DoorWidthMetres: double.NaN)), CirculationCodes.OptionsInvalid, "door_width_m");
        Refused("an empty entrance edge list is refused", Analyze(corridorBrief, corridor, new CirculationOptions(EntranceEdges: Array.Empty<LotEdge>())), CirculationCodes.OptionsInvalid, "entrance_edges");
        Refused("an undefined entrance edge is refused", Analyze(corridorBrief, corridor, new CirculationOptions(EntranceEdges: new[] { (LotEdge)9 })), CirculationCodes.OptionsInvalid, "entrance_edges[0]");
        Refused("a blank bedroom pattern is refused", Analyze(corridorBrief, corridor, new CirculationOptions(BedroomPatterns: new[] { " " })), CirculationCodes.OptionsInvalid, "bedroom_patterns[0]");
        var failedPlan = new SpacePlan(SpacePlanStatus.Infeasible, "corridor", 9, 1, new PlanFootprint(9, 3), 0.9, Array.Empty<PlannedRoom>(),
            new[] { new PlanConstraint(PlanCodes.SearchExhausted, "plan", true, false, "crafted") }, 0);
        Refused("an infeasible plan is refused", Analyze(corridorBrief, failedPlan), CirculationCodes.PlanInvalid, "plan");
        Refused("a feasible plan without rooms is refused", Analyze(corridorBrief, failedPlan with { Status = SpacePlanStatus.Feasible, Constraints = Array.Empty<PlanConstraint>() }),
            CirculationCodes.PlanInvalid, "plan");
        var nullThrows = false;
        try { CirculationAnalyzer.Analyze(corridorBrief, null!); } catch (ArgumentNullException) { nullThrows = true; }
        Check(nullThrows, "a null plan is a programming error (ArgumentNullException)");

        // ---- determinism: runs, room order, culture ----
        var canonical = CirculationSerializer.Serialize(loopModel);
        Check(CirculationSerializer.Serialize(Analyze(loopBrief, loop)) == canonical, "the same plan gives byte-identical circulation JSON");
        Check(CirculationSerializer.Serialize(Analyze(loopBrief, loop with { Rooms = loop.Rooms.Reverse().ToArray() })) == canonical, "the model does not depend on the order of the plan's rooms");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check(CirculationSerializer.Serialize(Analyze(loopBrief, loop)) == canonical, "circulation JSON does not depend on the current culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        using (var document = JsonDocument.Parse(canonical))
        {
            var top = document.RootElement;
            Check(top.GetProperty("status").GetString() == "valid" && top.GetProperty("entrance").GetProperty("to").GetString() == "hall" && top.GetProperty("entrance_edge").GetString() == "south"
                && top.GetProperty("doors").GetArrayLength() == 4 && top.GetProperty("routes").GetArrayLength() == 4 && top.GetProperty("checks").GetArrayLength() == loopModel.Checks.Count
                && top.GetProperty("routes")[0].GetProperty("length_m").GetDouble() == loopModel.Routes[0].LengthMetres, "canonical JSON carries entrance, doors, routes and checks exactly", canonical);
        }

        // ---- scoring: a seventh, explained criterion ----
        var middle = Plan(corridorBrief, 2, P("living", 0, 0, 3, 3), P("hall", 3, 0, 3, 3), P("bedroom_a", 6, 0, 3, 3));
        var middleModel = Analyze(corridorBrief, middle);
        Check(middleModel.IsValid && Near(Route(middleModel, "living")?.LengthMetres, s2 + 1.5) && Near(Route(middleModel, "bedroom_a")?.LengthMetres, s2 + 1.5),
            "a central hall shortens both routes to sqrt(4.5) + 1.5 m", Describe(middleModel));
        var pair = new[] { corridor, middle };
        var scored = CirculationRanker.Rank(corridorBrief, pair);
        var baseSet = AlternativeRanker.Rank(corridorBrief, pair);
        Check(scored.Status == AlternativeSetStatus.Ranked && scored.Ranked.Count == 2 && scored.Ranked[0].Index == 1 && scored.Ranked[1].Index == 0 && scored.Ranked[0].Total > scored.Ranked[1].Total,
            "with equal six-criterion scores the plan with shorter routes ranks first", Describe(scored));
        Check(baseSet.Ranked.Count == 2 && baseSet.Ranked[0].Total == baseSet.Ranked[1].Total, "the six base criteria alone tie the two corridors", Describe(baseSet));
        var lengthScale = Math.Sqrt(27);
        var corridorMean = (7.5 + 2 * s2) / 3;
        var middleMean = (4.5 + 2 * s2) / 3;
        var corridorCriterion = scored.Alternatives.First(a => a.Index == 0).Criteria.LastOrDefault();
        var middleCriterion = scored.Alternatives.First(a => a.Index == 1).Criteria.LastOrDefault();
        Check(corridorCriterion is { Criterion: CirculationCodes.Criterion } && Near(corridorCriterion.Raw, corridorMean) && Near(corridorCriterion.Normalized, lengthScale / (lengthScale + corridorMean))
            && middleCriterion is not null && Near(middleCriterion.Raw, middleMean) && Near(middleCriterion.Normalized, lengthScale / (lengthScale + middleMean)),
            "circulation raw is the mean route length and normalized sqrt(area) / (sqrt(area) + mean)", Describe(scored));
        Check(corridorCriterion is not null && corridorCriterion.Detail.Contains("mean route", StringComparison.Ordinal) && corridorCriterion.Detail.Contains("3 of 3", StringComparison.Ordinal),
            "the circulation criterion explains itself", corridorCriterion?.Detail ?? "");
        string? weightProblem = null;
        foreach (var alternative in scored.Ranked)
        {
            weightProblem ??= ExplanationProblem(alternative);
            var circulation = alternative.Criteria[alternative.Criteria.Count - 1];
            if (Math.Abs(circulation.Weight - 0.15 / 1.15) > 1e-15) weightProblem ??= "circulation weight " + R(circulation.Weight);
            if (Math.Abs(alternative.Criteria[0].Weight - 0.30 / 1.15) > 1e-15) weightProblem ??= "area_fit weight " + R(alternative.Criteria[0].Weight);
        }
        Check(weightProblem is null, "seven criteria with weights given / sum of all seven whose contributions sum to the total", weightProblem ?? "");
        Check(BaseAgrees(scored, baseSet) is null, "the six base criteria keep their raw and normalized values and details", BaseAgrees(scored, baseSet) ?? "");

        // ---- the hard circulation gate ----
        var gateBrief = Lot("gate", 12, 3, "hall", "living", "bedroom_a", "bedroom_b", "store");
        var gatePlans = new[]
        {
            Plan(gateBrief, 10, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("bedroom_a", 6, 0, 3, 3)),
            Plan(gateBrief, 11, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("bedroom_a", 6, 0, 3, 3), P("bedroom_b", 9, 0, 3, 3)),
            Plan(gateBrief, 12, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("store", 6, 2.5, 3, 0.5)),
        };
        var gated = CirculationRanker.Rank(gateBrief, gatePlans);
        var chainAlternative = gated.Alternatives.First(a => a.Index == 1);
        var storeAlternative = gated.Alternatives.First(a => a.Index == 2);
        Check(gated.Status == AlternativeSetStatus.Ranked && gated.Ranked.Count == 1 && gated.Ranked[0].Index == 0 && gated.Alternatives.Count == 3
            && gated.Alternatives.Skip(1).Select(a => a.Index).SequenceEqual(new[] { 1, 2 }), "only the plan with valid circulation is ranked; the others follow in generation order", Describe(gated));
        Check(chainAlternative is { Status: AlternativeStatus.Infeasible, Rank: 0, Total: 0.0 } && chainAlternative.Criteria.Count == 0
            && chainAlternative.Reasons.Any(r => r.StartsWith(CirculationCodes.BedroomAccess + "@bedroom_b: ", StringComparison.Ordinal)),
            "a bedroom behind a bedroom rejects the plan with circulation_bedroom_access", Describe(gated));
        Check(storeAlternative is { Status: AlternativeStatus.Infeasible } && storeAlternative.Reasons.Any(r => r.StartsWith(CirculationCodes.Reachable + "@store: ", StringComparison.Ordinal)),
            "an unreachable room rejects the plan with circulation_reachable", Describe(gated));
        var allGated = CirculationRanker.Rank(gateBrief, gatePlans.Skip(1).ToArray());
        Check(allGated.Status == AlternativeSetStatus.Infeasible && allGated.Ranked.Count == 0, "a set where every circulation fails is infeasible", Describe(allGated));
        var lenient = CirculationRanker.Rank(gateBrief, gatePlans, new CirculationScoring(RequireValid: false));
        var chainScore = lenient.Alternatives.First(a => a.Index == 1).Criteria.LastOrDefault();
        var storeScore = lenient.Alternatives.First(a => a.Index == 2).Criteria.LastOrDefault();
        Check(lenient.Ranked.Count == 3 && chainScore is not null && storeScore is not null
            && Near(chainScore.Raw, (31.5 + 2 * s2) / 4) && Near(chainScore.Normalized, 0.75 * 6 / (6 + (31.5 + 2 * s2) / 4))
            && Near(storeScore.Raw, (18.5 + s2) / 3) && Near(storeScore.Normalized, 2.0 / 3 * Math.Sqrt(19.5) / (Math.Sqrt(19.5) + (18.5 + s2) / 3)),
            "without the gate, a room without a valid route counts the penalty length sum(width + depth) and scales the score by the valid fraction", Describe(lenient));
        Check(chainScore is not null && chainScore.Detail.Contains("require_valid false", StringComparison.Ordinal) && chainScore.Detail.Contains("penalty", StringComparison.Ordinal),
            "the lenient criterion detail records the gate setting and the penalty", chainScore?.Detail ?? "");

        // ---- monotonicity: making a room unreachable never raises the score ----
        var monoBrief = Lot("mono", 9, 3, "hall", "living", "store");
        var mono = new[] { Plan(monoBrief, 40, P("hall", 0, 0, 3, 3), P("living", 3, 0, 3, 3), P("store", 6, 0, 3, 1)) };
        double LenientScore(double width) => CirculationRanker.Rank(monoBrief, mono, new CirculationScoring(RequireValid: false, Options: new CirculationOptions(DoorWidthMetres: width)))
            .Ranked.Single().Criteria.Last().Normalized;
        var reachableScore = LenientScore(0.9);
        var unreachableScore = LenientScore(1.2);
        var monoScale = Math.Sqrt(21);
        Check(Near(reachableScore, monoScale / (monoScale + (1.5 + 1.5 + s2 + s2 + Math.Sqrt(10) + 1.5) / 3))
            && Near(unreachableScore, 2.0 / 3 * monoScale / (monoScale + (1.5 + 1.5 + s2 + 16) / 3)) && unreachableScore < reachableScore,
            "a 1.2 m door that cuts the store off lowers the lenient score (same geometry, same routes otherwise)", R(reachableScore) + " -> " + R(unreachableScore));
        string? monoProblem = null;
        foreach (var plan in gatePlans)
        {
            var full = CirculationRanker.Rank(gateBrief, new[] { plan }, new CirculationScoring(RequireValid: false)).Ranked.Single().Criteria.Last().Normalized;
            var fullModel = Analyze(gateBrief, plan);
            foreach (var room in plan.Rooms.Where(r => r.InstanceId != "hall"))
            {
                // Only rooms with a valid route: shrink the room to a 0.5 m strip along its top side, so it loses every door.
                if (fullModel.Checks.Any(c => c.Subject == room.InstanceId && !c.Satisfied)) continue;
                var cut = plan with { Rooms = plan.Rooms.Select(r => r.InstanceId == room.InstanceId ? r with { Rect = r.Rect with { Y = r.Rect.Top - 0.5, Depth = 0.5 } } : r).ToArray() };
                if (Route(Analyze(gateBrief, cut), room.InstanceId) is { Reachable: true }) continue;
                var cutScore = CirculationRanker.Rank(gateBrief, new[] { cut }, new CirculationScoring(RequireValid: false)).Ranked.Single().Criteria.Last().Normalized;
                if (cutScore > full) monoProblem ??= "plan " + Text(plan.Seed) + " cutting " + room.InstanceId + " raised " + R(full) + " to " + R(cutScore);
            }
        }
        Check(monoProblem is null, "cutting any non-entrance room off never raises the lenient circulation score", monoProblem ?? "");

        // ---- provenance and refused details ----
        var strictJson = AlternativeSetSerializer.Serialize(CirculationRanker.Rank(corridorBrief, pair));
        var lenientJson = AlternativeSetSerializer.Serialize(CirculationRanker.Rank(corridorBrief, pair, new CirculationScoring(RequireValid: false)));
        Check(strictJson != lenientJson && strictJson.Contains("require_valid true", StringComparison.Ordinal) && strictJson.Contains("circulation weight 0.15", StringComparison.Ordinal),
            "the serialized set records the circulation weight and the gate setting, so runs with different settings differ", strictJson.Length.ToString(CultureInfo.InvariantCulture));
        var missingEntrance = CirculationRanker.Rank(gateBrief, new[] { gatePlans[0] }, new CirculationScoring(RequireValid: false, Options: new CirculationOptions(EntranceRoomId: "store")));
        var missingScore = missingEntrance.Ranked.SingleOrDefault()?.Criteria.Last();
        Check(missingScore is not null && missingScore.Normalized == 0 && missingScore.Detail.Contains(CirculationCodes.OptionsInvalid + "@entrance_room", StringComparison.Ordinal),
            "a refused circulation model explains the score with its refusal codes", missingScore?.Detail ?? Describe(missingEntrance));
        var noEntrance = CirculationRanker.Rank(corridorBrief, new[] { corridor }, new CirculationScoring(RequireValid: false, Options: new CirculationOptions(EntranceEdges: new[] { LotEdge.East })));
        var noEntranceScore = noEntrance.Ranked.SingleOrDefault()?.Criteria.Last();
        Check(noEntranceScore is not null && noEntranceScore.Normalized == 0 && noEntranceScore.Detail.Contains(CirculationCodes.Entrance + "@plan", StringComparison.Ordinal),
            "a missing entrance explains the zero score with the failed check code", noEntranceScore?.Detail ?? Describe(noEntrance));

        // ---- a two-level brief through Generate: upper levels never gate ----
        var duplex = new ProjectBrief(1, "duplex", new BriefConstraints(null, new BriefSite(new BriefValue<double>(6), new BriefValue<double>(6)), new BriefValue<int>(2)),
            new[]
            {
                new BriefRoom("hall", "Hall", null, new BriefValue<double>(8), new BriefValue<double>(6), null, new BriefValue<int>(0)),
                new BriefRoom("living", "Living", null, new BriefValue<double>(16), new BriefValue<double>(12), null, new BriefValue<int>(0)),
                new BriefRoom("bedroom_a", "Bedroom", null, new BriefValue<double>(12), new BriefValue<double>(9), null, new BriefValue<int>(1)),
                new BriefRoom("bath", "Bath", null, new BriefValue<double>(5), new BriefValue<double>(3), null, new BriefValue<int>(1)),
            },
            new[] { new BriefAdjacency("hall", "living", BriefAdjacencyKind.Required) });
        var duplexSet = CirculationRanker.Generate(duplex, new AlternativeOptions(6));
        string? duplexProblem = null;
        foreach (var alternative in duplexSet.Ranked)
        {
            var duplexModel = Analyze(duplex, alternative.Plan);
            if (!duplexModel.IsValid || duplexModel.Checks.Count(c => c.Code == CirculationCodes.Unsupported && !c.Hard) != alternative.Plan.Rooms.Count(r => r.Level != 0))
                duplexProblem ??= "index " + Text(alternative.Index) + ": " + Describe(duplexModel);
            if (Oracle(duplex, alternative.Plan, duplexModel, alternative.Plan.DoorWidthMetres, true) is { } problem) duplexProblem ??= "index " + Text(alternative.Index) + ": " + problem;
            if (!alternative.Criteria.Last().Detail.Contains("level 0", StringComparison.Ordinal)) duplexProblem ??= "criterion detail does not say only level 0 is scored";
        }
        foreach (var rejected in duplexSet.Alternatives.Where(a => a.Status == AlternativeStatus.Infeasible))
            if (rejected.Reasons.Any(r => r.Contains("bedroom_a", StringComparison.Ordinal) || r.Contains("bath", StringComparison.Ordinal) || r.Contains(CirculationCodes.Unsupported, StringComparison.Ordinal)))
                duplexProblem ??= "index " + Text(rejected.Index) + " rejected for an upper-level room: " + string.Join(", ", rejected.Reasons);
        Check(duplexSet.Status == AlternativeSetStatus.Ranked && duplexSet.Ranked.Count >= 1 && duplexProblem is null,
            "a two-level brief ranks through Generate; upper-level rooms are reported unsupported, never gating", (duplexProblem ?? "") + " " + Describe(duplexSet));

        // ---- weights ----
        foreach (var (label, weight) in new[] { ("negative", -0.1), ("NaN", double.NaN), ("infinite", double.PositiveInfinity) })
        {
            var refused = CirculationRanker.Rank(corridorBrief, pair, new CirculationScoring(CirculationWeight: weight));
            Check(refused.Status == AlternativeSetStatus.Refused && refused.Alternatives.Count == 0
                && refused.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid && r.Subject == "weights.circulation"), label + " circulation weight is refused", Describe(refused));
        }
        var onlyCirculation = CirculationRanker.Rank(corridorBrief, pair, new CirculationScoring(new ScoringWeights(0, 0, 0, 0, 0, 0), 1));
        Check(onlyCirculation.Status == AlternativeSetStatus.Ranked && onlyCirculation.Ranked[0].Index == 1 && onlyCirculation.Ranked.All(a => a.Criteria[a.Criteria.Count - 1].Weight == 1
            && a.Total == a.Criteria[a.Criteria.Count - 1].Contribution && a.Criteria.Take(6).All(c => c.Weight == 0)), "zero base weights with a positive circulation weight rank by circulation alone", Describe(onlyCirculation));
        var noneAtAll = CirculationRanker.Rank(corridorBrief, pair, new CirculationScoring(new ScoringWeights(0, 0, 0, 0, 0, 0), 0));
        Check(noneAtAll.Status == AlternativeSetStatus.Refused && noneAtAll.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid && r.Subject == "weights"),
            "all seven weights zero is refused", Describe(noneAtAll));
        var badBase = CirculationRanker.Rank(corridorBrief, pair, new CirculationScoring(new ScoringWeights(AreaFit: -1)));
        Check(badBase.Status == AlternativeSetStatus.Refused && badBase.Refusals.Any(r => r.Subject == "weights.area_fit"), "invalid base weights are still refused", Describe(badBase));

        // ---- apartment-2br: generated alternatives, oracle over every plan, backward compatibility ----
        var example = BriefParser.Parse(File.ReadAllText(Path.Combine(root, "examples", "brief", "apartment-2br.json")));
        const int n = 12;
        string? realProblem = null;
        var validCount = 0;
        for (var seed = 0; seed < n; seed++)
        {
            var plan = SpacePlanner.Plan(example, seed);
            if (!plan.IsFeasible) continue;
            var real = Analyze(example, plan);
            if (real.IsValid) validCount++;
            if (Oracle(example, plan, real, plan.DoorWidthMetres, true) is { } problem) realProblem ??= "seed " + Text(seed) + ": " + problem;
        }
        Check(realProblem is null, "every apartment-2br plan of 12 seeds agrees with the independent graph oracle", realProblem ?? "");
        Check(validCount >= 1, "at least one apartment-2br plan has valid circulation", Text(validCount) + " valid of " + Text(n));
        var options = new AlternativeOptions(n, BaseSeed: 0);
        var generated = CirculationRanker.Generate(example, options);
        Check(generated.Status == AlternativeSetStatus.Ranked && generated.Alternatives.Count == n && generated.Requested == n, "apartment-2br yields a ranked circulation set", Describe(generated));
        string? generatedProblem = null;
        for (var i = 0; i + 1 < generated.Ranked.Count; i++)
        {
            var (x, y) = (generated.Ranked[i], generated.Ranked[i + 1]);
            if (!(x.Total > y.Total || (x.Total == y.Total && x.Index < y.Index))) generatedProblem ??= "order at rank " + Text(x.Rank);
        }
        foreach (var alternative in generated.Ranked)
        {
            generatedProblem ??= ExplanationProblem(alternative);
            if (!Analyze(example, alternative.Plan).IsValid) generatedProblem ??= "ranked alternative " + Text(alternative.Index) + " has invalid circulation";
        }
        foreach (var rejected in generated.Alternatives.Where(a => a.Status == AlternativeStatus.Infeasible && a.Plan.IsFeasible))
            if (!rejected.Reasons.Any(r => r.StartsWith("circulation_", StringComparison.Ordinal))) generatedProblem ??= "feasible plan " + Text(rejected.Index) + " rejected without a circulation reason";
        Check(generatedProblem is null && generated.Ranked.Select(a => a.Rank).SequenceEqual(Enumerable.Range(1, generated.Ranked.Count)),
            "generated ranking is monotonic, explained, and gated on valid circulation", generatedProblem ?? "");
        var baseGenerated = AlternativeGenerator.Generate(example, options);
        Check(BaseAgrees(generated, baseGenerated) is null, "apartment-2br base criteria are unchanged by the circulation composition", BaseAgrees(generated, baseGenerated) ?? "");
        var zero = CirculationRanker.Generate(example, options, new CirculationScoring(CirculationWeight: 0, RequireValid: false));
        Check(zero.Alternatives.Select(a => a.Index + ":" + a.Status + ":" + Text(a.Rank) + ":" + R(a.Total)).SequenceEqual(baseGenerated.Alternatives.Select(a => a.Index + ":" + a.Status + ":" + Text(a.Rank) + ":" + R(a.Total))),
            "circulation weight 0 without the gate reproduces the six-criterion ranking and totals bit for bit", Describe(zero) + " || " + Describe(baseGenerated));
        var generatedJson = AlternativeSetSerializer.Serialize(generated);
        Check(AlternativeSetSerializer.Serialize(CirculationRanker.Generate(example, options)) == generatedJson
            && AlternativeSetSerializer.Serialize(CirculationRanker.Generate(example, options, new CirculationScoring(new ScoringWeights(), 0.15, true, new CirculationOptions()))) == generatedJson,
            "circulation ranking is byte-identical across runs and explicit defaults");
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check(AlternativeSetSerializer.Serialize(CirculationRanker.Generate(example, options)) == generatedJson, "circulation ranking does not depend on the current culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var badCount = CirculationRanker.Generate(example, new AlternativeOptions(0));
        Check(badCount.Status == AlternativeSetStatus.Refused && badCount.Refusals.Any(r => r.Code == AlternativeCodes.CountInvalid), "an invalid count is refused without planning", Describe(badCount));

        // ---- prototype layouts: the oracle holds on ranked 1/2/3-bedroom layouts ----
        string? prototypeProblem = null;
        var prototypePlans = 0;
        foreach (var typology in new[] { ApartmentTypology.OneBedroom, ApartmentTypology.TwoBedroom, ApartmentTypology.ThreeBedroom })
        {
            var result = PrototypeGenerator.Generate(new PrototypeRequest(typology, new PrototypeLot(8.66, 20), Alternatives: 3));
            if (result.Brief is null || result.Alternatives is null) { prototypeProblem ??= typology + " not generated"; continue; }
            foreach (var alternative in result.Alternatives.Ranked)
            {
                prototypePlans++;
                var prototypeModel = Analyze(result.Brief, alternative.Plan);
                if (Oracle(result.Brief, alternative.Plan, prototypeModel, alternative.Plan.DoorWidthMetres, true) is { } problem) prototypeProblem ??= typology + " index " + Text(alternative.Index) + ": " + problem;
            }
        }
        Check(prototypeProblem is null && prototypePlans >= 3, "ranked prototype layouts agree with the independent graph oracle", (prototypeProblem ?? "") + " plans " + Text(prototypePlans));
    }

    static CirculationModel Analyze(ProjectBrief brief, SpacePlan plan, CirculationOptions? options = null)
    {
        try { return CirculationAnalyzer.Analyze(brief, plan, options); }
        catch (Exception error)
        {
            return new CirculationModel(CirculationStatus.Refused, "threw", plan.Seed, 0, null, null, Array.Empty<CirculationDoor>(), Array.Empty<CirculationRoute>(),
                new[] { new PlanConstraint("threw " + error.GetType().Name, "plan", true, false, error.Message) });
        }
    }

    // Independent oracle: doors exactly for same-level pairs sharing at least the door width, each a door-wide segment on
    // both rooms' boundaries; the entrance on the lot edge and the entrance room's boundary, never a bedroom; reachability
    // and bedroom-free reachability by breadth-first search; every route a chain of real doors whose walked length is
    // recomputed; Used flags; status and checks consistent with the searches. Null when everything holds.
    static string? Oracle(ProjectBrief brief, SpacePlan plan, CirculationModel model, double door, bool defaultOptions)
    {
        var problems = new List<string>();
        if (model.Status == CirculationStatus.Refused) return "refused: " + Describe(model);
        const double e = 1e-7;
        var rooms = plan.Rooms.ToDictionary(r => r.InstanceId, StringComparer.Ordinal);
        bool Bedroom(PlannedRoom room) => room.RoomId.Contains("bedroom", StringComparison.OrdinalIgnoreCase) || room.Name.Contains("bedroom", StringComparison.OrdinalIgnoreCase)
            || room.RoomId.Contains("dormitorio", StringComparison.OrdinalIgnoreCase) || room.Name.Contains("dormitorio", StringComparison.OrdinalIgnoreCase);
        var expected = new SortedSet<string>(StringComparer.Ordinal);
        var neighbours = plan.Rooms.ToDictionary(r => r.InstanceId, _ => new List<string>(), StringComparer.Ordinal);
        for (var i = 0; i < plan.Rooms.Count; i++)
            for (var j = i + 1; j < plan.Rooms.Count; j++)
            {
                var (a, b) = (plan.Rooms[i], plan.Rooms[j]);
                if (a.Level != b.Level || Shared(a.Rect, b.Rect) < door - e) continue;
                var (from, to) = string.CompareOrdinal(a.InstanceId, b.InstanceId) < 0 ? (a.InstanceId, b.InstanceId) : (b.InstanceId, a.InstanceId);
                expected.Add(from + "|" + to);
                neighbours[a.InstanceId].Add(b.InstanceId);
                neighbours[b.InstanceId].Add(a.InstanceId);
            }
        if (string.Join(";", expected) != Pairs(model)) problems.Add("doors " + Pairs(model) + " != expected " + string.Join(";", expected));
        if (model.Doors.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != model.Doors.Count || model.Doors.Any(d => d.Id == CirculationModel.EntranceDoorId)) problems.Add("door ids not unique");
        foreach (var d in model.Doors)
        {
            if (!rooms.TryGetValue(d.From, out var a) || !rooms.TryGetValue(d.To, out var b)) { problems.Add("door " + d.Id + " names unknown rooms"); continue; }
            if (Math.Abs(Length(d) - door) > e || (d.X0 != d.X1 && d.Y0 != d.Y1)) problems.Add("door " + d.Id + " is not an axis-aligned door-wide segment");
            if (d.Level != a.Level || !OnBoundary(a.Rect, d.X0, d.Y0) || !OnBoundary(a.Rect, d.X1, d.Y1) || !OnBoundary(b.Rect, d.X0, d.Y0) || !OnBoundary(b.Rect, d.X1, d.Y1))
                problems.Add("door " + d.Id + " is not on the shared wall of " + d.From + " and " + d.To);
        }
        var lotWidth = brief.Constraints.Site?.WidthMetres.Value ?? plan.Footprint!.WidthMetres;
        var lotDepth = brief.Constraints.Site?.DepthMetres.Value ?? plan.Footprint!.DepthMetres;
        string? start = null;
        if (model.Entrance is { } entrance)
        {
            start = entrance.To;
            if (!rooms.TryGetValue(entrance.To, out var room)) problems.Add("entrance room unknown");
            else
            {
                if (entrance.From != CirculationModel.Exterior || entrance.Id != CirculationModel.EntranceDoorId || Math.Abs(Length(entrance) - door) > e || room.Level != entrance.Level)
                    problems.Add("entrance door malformed");
                if (!OnBoundary(room.Rect, entrance.X0, entrance.Y0) || !OnBoundary(room.Rect, entrance.X1, entrance.Y1)) problems.Add("entrance not on the entrance room's boundary");
                var onEdge = model.EntranceEdge switch
                {
                    LotEdge.South => Math.Abs(entrance.Y0) < e && Math.Abs(entrance.Y1) < e,
                    LotEdge.North => Math.Abs(entrance.Y0 - lotDepth) < e && Math.Abs(entrance.Y1 - lotDepth) < e,
                    LotEdge.West => Math.Abs(entrance.X0) < e && Math.Abs(entrance.X1) < e,
                    LotEdge.East => Math.Abs(entrance.X0 - lotWidth) < e && Math.Abs(entrance.X1 - lotWidth) < e,
                    _ => false,
                };
                if (!onEdge) problems.Add("entrance not on the reported lot edge");
                if (defaultOptions && Bedroom(room)) problems.Add("a bedroom was chosen as entrance");
            }
        }
        else if (model.EntranceEdge is not null) problems.Add("entrance edge without entrance");
        HashSet<string> Search(bool avoidBedrooms)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (start is null || !rooms.ContainsKey(start)) return seen;
            var queue = new Queue<string>();
            seen.Add(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (avoidBedrooms && Bedroom(rooms[current])) continue;
                foreach (var next in neighbours[current]) if (seen.Add(next)) queue.Enqueue(next);
            }
            return seen;
        }
        var any = Search(false);
        var clear = Search(true);
        if (!model.Routes.Select(r => r.Instance).SequenceEqual(plan.Rooms.Select(r => r.InstanceId).OrderBy(id => id, StringComparer.Ordinal))) problems.Add("routes are not one per room in instance order");
        var used = new HashSet<string>(StringComparer.Ordinal);
        var doorsById = model.Doors.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var route in model.Routes)
        {
            if (!rooms.TryGetValue(route.Instance, out var target)) continue;
            if (route.IsBedroom != Bedroom(target)) problems.Add(route.Instance + " bedroom flag");
            if (route.Reachable != any.Contains(route.Instance)) problems.Add(route.Instance + " reachable " + route.Reachable + " but search says " + any.Contains(route.Instance));
            if (route.AvoidsBedrooms != clear.Contains(route.Instance)) problems.Add(route.Instance + " avoids bedrooms " + route.AvoidsBedrooms + " but search says " + clear.Contains(route.Instance));
            if (!route.Reachable)
            {
                if (route.Rooms.Count != 0 || route.Doors.Count != 0 || double.IsFinite(route.LengthMetres)) problems.Add(route.Instance + " unreachable route carries a path");
                continue;
            }
            if (route.Rooms.Count == 0 || route.Rooms[0] != start || route.Rooms[route.Rooms.Count - 1] != route.Instance || route.Doors.Count != route.Rooms.Count || route.Doors[0] != CirculationModel.EntranceDoorId)
            {
                problems.Add(route.Instance + " route malformed");
                continue;
            }
            if (route.AvoidsBedrooms && route.Rooms.Take(route.Rooms.Count - 1).Any(id => rooms.TryGetValue(id, out var r) && Bedroom(r))) problems.Add(route.Instance + " route passes a bedroom");
            var entranceDoor = model.Entrance!;
            var (x, y) = (entranceDoor.MidX, entranceDoor.MidY);
            double walked = 0;
            used.Add(CirculationModel.EntranceDoorId);
            for (var k = 1; k < route.Doors.Count; k++)
            {
                if (!doorsById.TryGetValue(route.Doors[k], out var crossed)) { problems.Add(route.Instance + " route names unknown door"); walked = double.NaN; break; }
                var (p, q) = (route.Rooms[k - 1], route.Rooms[k]);
                if (!((crossed.From == p && crossed.To == q) || (crossed.From == q && crossed.To == p))) problems.Add(route.Instance + " door " + crossed.Id + " does not join " + p + " and " + q);
                used.Add(crossed.Id);
                walked += Distance(x, y, crossed.MidX, crossed.MidY);
                (x, y) = (crossed.MidX, crossed.MidY);
            }
            var cx = target.Rect.X + target.Rect.Width / 2;
            var cy = target.Rect.Y + target.Rect.Depth / 2;
            walked += Distance(x, y, cx, cy);
            if (!(Math.Abs(walked - route.LengthMetres) <= 1e-9 * Math.Max(1, walked))) problems.Add(route.Instance + " length " + R(route.LengthMetres) + " != walked " + R(walked));
            if (route.LengthMetres < Distance(entranceDoor.MidX, entranceDoor.MidY, cx, cy) - 1e-9) problems.Add(route.Instance + " shorter than the straight line");
        }
        foreach (var d in model.Doors) if (d.Used != used.Contains(d.Id)) problems.Add("door " + d.Id + " used flag");
        if (model.Entrance is { } usedEntrance && usedEntrance.Used != used.Contains(CirculationModel.EntranceDoorId)) problems.Add("entrance used flag");
        var ground = plan.Rooms.Where(r => r.Level == 0).ToArray();
        var dry = ground.Where(r => !Bedroom(r) && !Wet(r)).ToArray();
        var valid = start is not null && ground.All(r => any.Contains(r.InstanceId)) && ground.Where(Bedroom).All(r => clear.Contains(r.InstanceId)) && dry.All(r => clear.Contains(r.InstanceId));
        if (valid != model.IsValid || model.IsValid != model.Checks.Where(c => c.Hard).All(c => c.Satisfied)) problems.Add("status " + model.Status + " but oracle says valid=" + valid);
        if (model.Checks.Count(c => c.Code == CirculationCodes.Reachable) != ground.Length || model.Checks.Count(c => c.Code == CirculationCodes.BedroomAccess) != ground.Count(Bedroom)
            || model.Checks.Count(c => c.Code == CirculationCodes.Passage) != dry.Length
            || model.Checks.Count(c => c.Code == CirculationCodes.Unsupported && !c.Hard && !c.Satisfied) != plan.Rooms.Count - ground.Length
            || model.Checks.Count(c => !c.Hard) != plan.Rooms.Count - ground.Length)
            problems.Add("check counts");
        foreach (var item in model.Checks.Where(c => c.Code == CirculationCodes.Reachable)) if (item.Satisfied != any.Contains(item.Subject)) problems.Add("reachable check " + item.Subject);
        foreach (var item in model.Checks.Where(c => c.Code == CirculationCodes.BedroomAccess || c.Code == CirculationCodes.Passage))
            if (item.Satisfied != clear.Contains(item.Subject)) problems.Add(item.Code + " check " + item.Subject);
        if (defaultOptions && start is not null && rooms.TryGetValue(start, out var front) && Wet(front) && !EntranceNamed(front)) problems.Add("a wet room was chosen as fallback entrance");

        // Independent minimality: Floyd-Warshall over the entrance, door midpoints and room centres. Inside a room every
        // pair of its doors is joined in both directions and every door leads to the room centre (a sink, so a route
        // ends there); in the bedroom-free graph a bedroom keeps only its door-to-centre edges.
        if (model.Entrance is { } door0)
        {
            var nodes = new List<(double X, double Y)> { (door0.MidX, door0.MidY) };
            var doorNode = new Dictionary<string, int>(StringComparer.Ordinal) { [CirculationModel.EntranceDoorId] = 0 };
            foreach (var d in model.Doors) { doorNode[d.Id] = nodes.Count; nodes.Add((d.MidX, d.MidY)); }
            var centreNode = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in plan.Rooms) { centreNode[r.InstanceId] = nodes.Count; nodes.Add((r.Rect.X + r.Rect.Width / 2, r.Rect.Y + r.Rect.Depth / 2)); }
            double[,] Shortest(bool avoid)
            {
                var size = nodes.Count;
                var w = new double[size, size];
                for (var i = 0; i < size; i++) for (var j = 0; j < size; j++) w[i, j] = i == j ? 0 : double.PositiveInfinity;
                foreach (var r in plan.Rooms)
                {
                    var own = model.Doors.Where(d => d.From == r.InstanceId || d.To == r.InstanceId).Select(d => doorNode[d.Id]).ToList();
                    if (r.InstanceId == start) own.Add(0);
                    var c = centreNode[r.InstanceId];
                    foreach (var u in own)
                    {
                        w[u, c] = Math.Min(w[u, c], Distance(nodes[u].X, nodes[u].Y, nodes[c].X, nodes[c].Y));
                        if (avoid && Bedroom(r)) continue;
                        foreach (var v in own) if (u != v) w[u, v] = Math.Min(w[u, v], Distance(nodes[u].X, nodes[u].Y, nodes[v].X, nodes[v].Y));
                    }
                }
                for (var k = 0; k < size; k++)
                    for (var i = 0; i < size; i++)
                        for (var j = 0; j < size; j++)
                            if (w[i, k] + w[k, j] < w[i, j]) w[i, j] = w[i, k] + w[k, j];
                return w;
            }
            var anyWalk = Shortest(false);
            var clearWalk = Shortest(true);
            foreach (var route in model.Routes.Where(r => r.Reachable))
            {
                var c = centreNode[route.Instance];
                var best = route.AvoidsBedrooms ? clearWalk[0, c] : anyWalk[0, c];
                if (!(Math.Abs(best - route.LengthMetres) <= 1e-9 * Math.Max(1, best))) problems.Add(route.Instance + " length " + R(route.LengthMetres) + " is not the shortest " + R(best));
                if (!route.AvoidsBedrooms && double.IsFinite(clearWalk[0, c])) problems.Add(route.Instance + " has a bedroom-free walk but its route passes a bedroom");
            }
        }
        return problems.Count == 0 ? null : string.Join("; ", problems.Take(6));
    }

    // Independent wet-room (pass-through) and entrance-name tests on room id or name, case-insensitive.
    static bool Wet(PlannedRoom room) => new[] { room.RoomId, room.Name }.Any(text =>
        new[] { "bath", "baño", "bano", "toilet", "ensuite", "en-suite", "vestidor", "closet", "dressing" }.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
        || text.StartsWith("wc", StringComparison.OrdinalIgnoreCase));

    static bool EntranceNamed(PlannedRoom room) => new[] { room.RoomId, room.Name }.Any(text => text.StartsWith("hall", StringComparison.OrdinalIgnoreCase)
        || new[] { "entrance", "entry", "foyer", "lobby", "vestibul", "palier" }.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)));

    // Base criteria of every ranked circulation alternative equal those of the same plan in the base set.
    static string? BaseAgrees(AlternativeSet scored, AlternativeSet baseSet)
    {
        foreach (var alternative in scored.Ranked)
        {
            var original = baseSet.Alternatives.FirstOrDefault(a => a.Index == alternative.Index);
            if (original is null || original.Status != AlternativeStatus.Ranked) return "alternative " + Text(alternative.Index) + " ranked here but not in the base set";
            if (alternative.Criteria.Count != 7) return "alternative " + Text(alternative.Index) + " has " + Text(alternative.Criteria.Count) + " criteria";
            for (var k = 0; k < 6; k++)
            {
                var (mine, theirs) = (alternative.Criteria[k], original.Criteria[k]);
                if (mine.Criterion != theirs.Criterion || mine.Raw != theirs.Raw || mine.Normalized != theirs.Normalized || mine.Detail != theirs.Detail)
                    return "alternative " + Text(alternative.Index) + " criterion " + theirs.Criterion + " changed";
            }
        }
        return null;
    }

    static string? ExplanationProblem(Alternative alternative)
    {
        if (!alternative.Criteria.Select(c => c.Criterion).SequenceEqual(BaseCriteria.Append(CirculationCodes.Criterion))) return "criteria " + string.Join(",", alternative.Criteria.Select(c => c.Criterion));
        double total = 0;
        foreach (var criterion in alternative.Criteria)
        {
            if (!(criterion.Normalized >= 0 && criterion.Normalized <= 1) || criterion.Contribution != criterion.Weight * criterion.Normalized || criterion.Detail.Length == 0)
                return criterion.Criterion + " normalized " + R(criterion.Normalized) + " contribution " + R(criterion.Contribution);
            total += criterion.Contribution;
        }
        if (Math.Min(1, total) != alternative.Total) return "total " + R(alternative.Total) + " != sum " + R(total);
        if (Math.Abs(alternative.Criteria.Sum(c => c.Weight) - 1) > 1e-12) return "weights sum " + R(alternative.Criteria.Sum(c => c.Weight));
        return null;
    }

    static PlannedRoom P(string id, double x, double y, double width, double depth, int level = 0) => new(id, id, id, 1, level, new PlanRect(x, y, width, depth));

    static ProjectBrief Lot(string name, double width, double depth, params string[] rooms) =>
        new(1, name, new BriefConstraints(null, new BriefSite(new BriefValue<double>(width), new BriefValue<double>(depth))), rooms.Select(id => new BriefRoom(id, id)).ToArray(),
            Array.Empty<BriefAdjacency>());

    static SpacePlan Plan(ProjectBrief brief, int seed, params PlannedRoom[] rooms) =>
        new(SpacePlanStatus.Feasible, brief.Name, seed, 1, new PlanFootprint(brief.Constraints.Site!.WidthMetres.Value, brief.Constraints.Site.DepthMetres.Value), 0.9,
            rooms, Array.Empty<PlanConstraint>(), 0);

    static CirculationRoute? Route(CirculationModel model, string instance) => model.Routes.FirstOrDefault(r => r.Instance == instance);

    static string Pairs(CirculationModel model) => string.Join(";", model.Doors.Select(d => d.From + "|" + d.To).OrderBy(p => p, StringComparer.Ordinal));

    static double Length(CirculationDoor door) => Distance(door.X0, door.Y0, door.X1, door.Y1);

    static double Distance(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    static bool Near(double? value, double expected) => value is { } v && Math.Abs(v - expected) <= Tolerance;

    static bool OnBoundary(PlanRect rect, double x, double y)
    {
        const double e = 1e-7;
        var inside = x >= rect.X - e && x <= rect.Right + e && y >= rect.Y - e && y <= rect.Top + e;
        return inside && (Math.Abs(x - rect.X) < e || Math.Abs(x - rect.Right) < e || Math.Abs(y - rect.Y) < e || Math.Abs(y - rect.Top) < e);
    }

    static double Shared(PlanRect a, PlanRect b)
    {
        const double e = 1e-7;
        if (Math.Abs(a.Right - b.X) < e || Math.Abs(b.Right - a.X) < e) return Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        if (Math.Abs(a.Top - b.Y) < e || Math.Abs(b.Top - a.Y) < e) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        return 0;
    }

    static string Describe(CirculationModel model) => model.Status + " entrance " + (model.Entrance is { } d ? d.To + "@" + model.EntranceEdge + "(" + R(d.MidX) + "," + R(d.MidY) + ")" : "none")
        + " doors " + Pairs(model) + " routes " + string.Join(" ", model.Routes.Select(r => r.Instance + "=" + R(r.LengthMetres) + (r.Reachable ? "" : "!") + (r.AvoidsBedrooms ? "" : "~") + "[" + string.Join(">", r.Rooms) + "]"))
        + " | " + string.Join("; ", model.Checks.Where(c => !c.Satisfied).Take(4).Select(c => c.Code + "@" + c.Subject + " " + c.Detail));

    static string Describe(AlternativeSet set) => set.Status + " " + string.Join(" | ", set.Refusals.Select(r => r.Code + "@" + r.Subject)) + " | "
        + string.Join(" | ", set.Alternatives.Take(8).Select(a => Text(a.Index) + ":" + a.Status + " r" + Text(a.Rank) + " t" + R(a.Total) + " " + string.Join(",", a.Reasons.Take(2))));

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
