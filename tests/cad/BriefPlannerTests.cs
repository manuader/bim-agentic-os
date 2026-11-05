using Bim.Cad;
using Bim.Core;
using Bim.Generative;
using System.Globalization;
using System.Text;

// P6-08 brief draft to planner brief oracle: the P6-05 draft of brief-plan.dxf becomes a validated P6-03 planner brief.
// Rooms below the confidence threshold, area/label mismatches and later duplicate labels are dropped with typed reasons
// (low-confidence, area-mismatch, duplicate-label) unless the caller confirms the room or the reason; the result
// validates with BriefValidator, round-trips through BriefSerializer/BriefParser and the P7-01 SpacePlanner plans it
// feasibly on a caller-given site. Deterministic across runs and cultures. Unit-tested evidence only.
internal static class BriefPlannerTests
{
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Ids(IEnumerable<BriefPlannerRoom> rooms) => string.Join(",", rooms.Select(r => r.Id));
    private static string Show(BriefPlannerRoom r) => r.Id + " c" + F(r.Confidence) + " [" + string.Join(",", r.Reasons) + "] confirmed [" + string.Join(",", r.Confirmed) + "] open [" + string.Join(",", r.Unconfirmed) + "]";
    private static string Show(BriefPlannerResult r) => "kept " + string.Join(" | ", r.Kept.Select(Show)) + " ; dropped " + string.Join(" | ", r.Dropped.Select(Show)) + " ; issues " + string.Join(",", r.Issues.Select(i => i.Code + "@" + i.Field));
    private static string PlanIds(SpacePlan plan) => string.Join(",", plan.Rooms.Select(r => r.RoomId).OrderBy(id => id, StringComparer.Ordinal));
    private static string Sorted(IEnumerable<string> ids) => string.Join(",", ids.OrderBy(id => id, StringComparer.Ordinal));

    private static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    private static string Text(string handle, double x, double y, string value) =>
        G("0 TEXT|5 " + handle + "|8 ROOM-TEXT|10 " + F(x) + "|20 " + F(y) + "|40 200|1 " + value);
    private static BriefDraft Dxf(string entities, BriefExtractOptions? options = null) =>
        BriefExtract.FromDxf(DxfReader.Parse(G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC|0 SECTION|2 ENTITIES") + entities + G("0 ENDSEC|0 EOF"), "gen.dxf"), options);

    private static Type? ErrorType(Func<object?> action)
    {
        try { action(); return null; }
        catch (Exception error) { return error.GetType(); }
    }

    private static BriefConstraints Site(double width, double depth) => new(Site: new BriefSite(new BriefValue<double>(width), new BriefValue<double>(depth)));

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "planner-brief " + name, detail);

        // ---------- brief-plan.dxf with default options ----------
        try
        {
            var draft = BriefExtract.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, "brief-plan.dxf")));
            var r = BriefPlanner.ToPlannerBrief(draft);
            C(BriefPlannerOptions.DefaultMinConfidence == 0.5 && new BriefPlannerOptions().MinConfidence == 0.5, "default threshold is 0.5");
            C(Ids(r.Kept) == "dormitorio-1,cocina,living-room,lavadero,hall" && Ids(r.Dropped) == "bano,bano-2", "defaults keep five rooms and drop the mismatched baño and the later duplicate (draft order)", Show(r));
            var bano = r.Dropped.FirstOrDefault(x => x.Id == "bano");
            C(bano is not null && bano.Name == "BAÑO" && bano.Reasons.SequenceEqual(new[] { BriefPlannerCodes.AreaMismatch, BriefPlannerCodes.LowConfidence }) && bano.Confirmed.Count == 0
                && bano.Unconfirmed.SequenceEqual(bano.Reasons) && Math.Abs(bano.Confidence - 0.95 * 0.6 * 0.8) <= 1e-12,
                "baño (first of the duplicates) is dropped for area-mismatch and low-confidence, not duplicate-label", bano is null ? Show(r) : Show(bano));
            var bano2 = r.Dropped.FirstOrDefault(x => x.Id == "bano-2");
            C(bano2 is not null && bano2.Reasons.SequenceEqual(new[] { BriefPlannerCodes.DuplicateLabel }), "baño 2 is dropped as a duplicate-label only", bano2 is null ? Show(r) : Show(bano2));
            C(r.Kept.All(k => k.Reasons.Count == 0 && k.Confirmed.Count == 0 && k.Unconfirmed.Count == 0), "kept rooms carry no reasons by default", Show(r));
            C(BriefPlannerCodes.All.SequenceEqual(new[] { "area-mismatch", "duplicate-label", "low-confidence" }), "reason codes are typed constants in ordinal order", string.Join(",", BriefPlannerCodes.All));

            var brief = r.Brief;
            var draftRooms = draft.Brief.Rooms.ToDictionary(x => x.Id, StringComparer.Ordinal);
            C(brief.Rooms.Select(x => x.Id).SequenceEqual(new[] { "dormitorio-1", "cocina", "living-room", "lavadero", "hall" })
                && brief.Rooms.All(x => draftRooms.TryGetValue(x.Id, out var original) && original == x),
                "planner brief keeps the draft's room records (areas, provenance) in draft order", BriefSerializer.Serialize(brief));
            C(brief.Name == draft.Brief.Name && brief.Version == ProjectBrief.CurrentVersion && brief.Constraints == draft.Brief.Constraints && brief.Adjacencies.Count == 0,
                "planner brief keeps the draft name and constraints by default", brief.Name);
            C(r.IsValid && r.Issues.Count == 0 && BriefValidator.Validate(brief).Length == 0, "planner brief passes BriefValidator", Show(r));
            var json = BriefSerializer.Serialize(brief);
            C(BriefSerializer.Serialize(BriefParser.Parse(json)) == json, "planner brief round-trips through BriefSerializer and BriefParser", json);
            C(r.UnknownConfirmedRooms.Count == 0 && r.UnknownConfirmedReasons.Count == 0, "no unknown confirmations by default");

            // P7-01 plans the result on a caller-given site.
            var sited = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = Site(10, 8), Name = "Casa Ader" });
            C(sited.Brief.Name == "Casa Ader" && sited.Brief.Constraints.Site is { } s && s.WidthMetres.Value == 10 && s.DepthMetres.Value == 8 && sited.IsValid,
                "name and constraints options replace the draft's", Show(sited));
            var plan = SpacePlanner.Plan(sited.Brief, 7);
            C(plan.IsFeasible && PlanIds(plan) == Sorted(sited.Kept.Select(k => k.Id)) && plan.HardViolations.Count == 0, "space planner plans the kept rooms feasibly",
                plan.Status + " " + PlanIds(plan) + " " + string.Join(";", plan.Violations.Select(v => v.Code + ":" + v.Subject + ":" + v.Detail)));
            C(plan.Rooms.All(p => p.Rect.X >= 0 && p.Rect.Y >= 0 && p.Rect.Right <= 10 + 1e-9 && p.Rect.Top <= 8 + 1e-9 && p.Rect.Area > 0), "planned rooms lie inside the site",
                string.Join(";", plan.Rooms.Select(p => p.RoomId + " " + p.Rect)));
            var again = SpacePlanner.Plan(BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = Site(10, 8), Name = "Casa Ader" }).Brief, 7);
            C(SpacePlanSerializer.Serialize(plan) == SpacePlanSerializer.Serialize(again), "same draft, options and seed give the same plan");
            var unsited = SpacePlanner.Plan(r.Brief, 7);
            C(!unsited.IsFeasible && unsited.Violations.Any(v => v.Code == PlanCodes.FootprintMissing), "without a site the planner refuses with footprint_missing (typed)", string.Join(",", unsited.Violations.Select(v => v.Code)));
            var footprint = SpacePlanner.Plan(r.Brief, 7, new PlannerOptions(Footprint: new PlanFootprint(10, 8)));
            C(footprint.IsFeasible && PlanIds(footprint) == Sorted(r.Kept.Select(k => k.Id)), "planner footprint option plans the default brief", footprint.Status.ToString());

            // Confirmations.
            var confirmed = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { ConfirmedRooms = new[] { "bano", "bano-2" }, Constraints = Site(10, 8) });
            C(Ids(confirmed.Kept) == "dormitorio-1,cocina,bano,living-room,bano-2,lavadero,hall" && confirmed.Dropped.Count == 0 && confirmed.IsValid, "confirmed rooms are kept in draft order", Show(confirmed));
            var keptBano = confirmed.Kept.FirstOrDefault(x => x.Id == "bano");
            C(keptBano is not null && keptBano.Reasons.SequenceEqual(new[] { "area-mismatch", "low-confidence" }) && keptBano.Confirmed.SequenceEqual(keptBano.Reasons) && keptBano.Unconfirmed.Count == 0,
                "a confirmed room records the reasons it was kept despite", keptBano is null ? Show(confirmed) : Show(keptBano));
            var target = confirmed.Brief.Rooms.FirstOrDefault(x => x.Id == "bano")?.TargetAreaSquareMetres?.Value;
            C(target == 6.5, "a confirmed mismatched room keeps the stated area as its target", target?.ToString(CultureInfo.InvariantCulture) ?? "null");
            var planAll = SpacePlanner.Plan(confirmed.Brief, 3);
            C(planAll.IsFeasible && planAll.Rooms.Count == 7 && PlanIds(planAll) == Sorted(confirmed.Kept.Select(k => k.Id)), "space planner plans all seven confirmed rooms",
                planAll.Status + " " + string.Join(";", planAll.Violations.Select(v => v.Code + ":" + v.Subject)));

            var reasonDup = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { ConfirmedReasons = new[] { BriefPlannerCodes.DuplicateLabel } });
            C(Ids(reasonDup.Dropped) == "bano" && reasonDup.Kept.Any(x => x.Id == "bano-2" && x.Confirmed.SequenceEqual(new[] { "duplicate-label" })), "confirming the duplicate-label reason keeps every later duplicate", Show(reasonDup));
            var reasonMismatch = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { ConfirmedReasons = new[] { BriefPlannerCodes.AreaMismatch } });
            var partly = reasonMismatch.Dropped.FirstOrDefault(x => x.Id == "bano");
            C(partly is not null && partly.Confirmed.SequenceEqual(new[] { "area-mismatch" }) && partly.Unconfirmed.SequenceEqual(new[] { "low-confidence" }), "a room stays dropped while any reason is unconfirmed", Show(reasonMismatch));
            var both = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { ConfirmedReasons = new[] { "low-confidence", "area-mismatch" } });
            C(Ids(both.Dropped) == "bano-2" && both.Kept.Any(x => x.Id == "bano"), "confirming every reason of a room keeps it", Show(both));

            var unknown = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { ConfirmedRooms = new[] { "zz", "cocina", "aa", "zz" }, ConfirmedReasons = new[] { "bogus", "duplicate-label", "Area-Mismatch" } });
            C(unknown.UnknownConfirmedRooms.SequenceEqual(new[] { "aa", "zz" }) && unknown.UnknownConfirmedReasons.SequenceEqual(new[] { "Area-Mismatch", "bogus" }),
                "unknown confirmations are reported distinct and ordinal-sorted (ids and codes are case-sensitive)", string.Join(",", unknown.UnknownConfirmedRooms) + " / " + string.Join(",", unknown.UnknownConfirmedReasons));
            C(Ids(unknown.Dropped) == "bano", "a confirmation of a room without reasons is not unknown and changes nothing", Show(unknown));

            // Threshold.
            var none = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { MinConfidence = 0 });
            var noneBano = none.Dropped.FirstOrDefault(x => x.Id == "bano");
            C(noneBano is not null && noneBano.Reasons.SequenceEqual(new[] { "area-mismatch" }) && Ids(none.Dropped) == "bano,bano-2", "threshold 0 drops only mismatches and duplicates", Show(none));
            var strict = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { MinConfidence = 0.96 });
            C(Ids(strict.Kept) == "cocina" && strict.Dropped.Where(x => x.Id != "bano" && x.Id != "bano-2").All(x => x.Reasons.SequenceEqual(new[] { "low-confidence" })), "threshold 0.96 keeps only the certain kitchen", Show(strict));
            var edge = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { MinConfidence = draft.Rooms[0].Confidence });
            C(edge.Kept.Any(x => x.Id == "dormitorio-1"), "a confidence equal to the threshold is kept", Show(edge));
            var all = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { MinConfidence = 1, ConfirmedRooms = new[] { "cocina" } });
            var everything = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { MinConfidence = 1 });
            C(Ids(all.Kept) == "cocina" && all.IsValid, "threshold 1 keeps a confidence-1 room", Show(all));

            // Every room dropped: the brief is typed invalid and the planner refuses it without throwing.
            var emptied = BriefPlanner.ToPlannerBrief(Dxf(Text("1", 0, 0, "Cocina")), new BriefPlannerOptions { MinConfidence = 0.6, Constraints = Site(10, 8) });
            C(emptied.Kept.Count == 0 && emptied.Dropped.Count == 1 && emptied.Dropped[0].Reasons.SequenceEqual(new[] { "low-confidence" }) && !emptied.IsValid
                && emptied.Issues.Any(i => i.Code == BriefCodes.RoomsEmpty && i.Severity == BriefSeverity.Error) && emptied.Brief.Rooms.Count == 0,
                "dropping every room gives an invalid brief flagged rooms_empty", Show(emptied));
            var refused = SpacePlanner.Plan(emptied.Brief, 1);
            C(!refused.IsFeasible && refused.Violations.Any(v => v.Code == PlanCodes.BriefInvalid), "space planner refuses the empty brief as brief_invalid", string.Join(",", refused.Violations.Select(v => v.Code)));

            var budget = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = new BriefConstraints(Levels: new BriefValue<int>(0)) });
            C(!budget.IsValid && budget.Issues.Any(i => i.Code == BriefCodes.LevelsInvalid), "invalid caller constraints are reported by the validator", Show(budget));

            // Determinism across runs and culture.
            var culture = CultureInfo.CurrentCulture;
            string de;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var d2 = BriefExtract.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, "brief-plan.dxf")));
                de = BriefSerializer.Serialize(BriefPlanner.ToPlannerBrief(d2).Brief) + Show(BriefPlanner.ToPlannerBrief(d2));
            }
            finally { CultureInfo.CurrentCulture = culture; }
            C(de == json + Show(r) && BriefSerializer.Serialize(BriefPlanner.ToPlannerBrief(draft).Brief) == json, "deterministic across runs and de-DE culture");

            // Argument checks.
            C(ErrorType(() => BriefPlanner.ToPlannerBrief(null!)) == typeof(ArgumentNullException), "null draft refused");
            foreach (var bad in new[] { double.NaN, -0.01, 1.01, double.PositiveInfinity })
                C(ErrorType(() => BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { MinConfidence = bad })) == typeof(ArgumentOutOfRangeException), "threshold " + F(bad) + " refused");
            C(ErrorType(() => BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { ConfirmedRooms = null!, ConfirmedReasons = null! })) is null, "null confirmation collections mean none");
            C(everything.Kept.Count == 1, "threshold 1 without confirmation keeps only confidence-1 rooms", Show(everything));
        }
        catch (Exception error) { C(false, "fixture unexpected", error.GetType().Name + ": " + error.Message + " " + error.StackTrace); }

        // ---------- generated draft: three duplicate labels ----------
        try
        {
            var draft = Dxf(Text("1", 0, 0, "Baño 4 m²") + Text("2", 20000, 0, "BAÑO 5 m²") + Text("3", 40000, 0, "bano 6 m²") + Text("4", 60000, 0, "Cocina 9 m²"));
            C(draft.Rooms.Select(x => x.Id).SequenceEqual(new[] { "bano", "bano-2", "bano-3", "cocina" }), "gen draft has three duplicate baños", string.Join(",", draft.Rooms.Select(x => x.Id)));
            var r = BriefPlanner.ToPlannerBrief(draft, new BriefPlannerOptions { Constraints = Site(6, 5) });
            C(Ids(r.Kept) == "bano,cocina" && Ids(r.Dropped) == "bano-2,bano-3" && r.Dropped.All(x => x.Reasons.SequenceEqual(new[] { "duplicate-label" })) && r.IsValid,
                "gen the first label is kept and every later duplicate dropped", Show(r));
            var plan = SpacePlanner.Plan(r.Brief, 11);
            C(plan.IsFeasible && PlanIds(plan) == "bano,cocina", "gen space planner plans the deduplicated brief", plan.Status.ToString());
            var capped = Dxf(Text("1", 0, 0, "Baño 4 m²") + Text("2", 20000, 0, "BAÑO 5 m²") + Text("3", 40000, 0, "bano 6 m²") + Text("5", 80000, 0, "17 m²"), new BriefExtractOptions { MaxUncertainties = 1 });
            var cappedResult = BriefPlanner.ToPlannerBrief(capped);
            C(Ids(cappedResult.Dropped) == "bano-2,bano-3", "gen duplicates are found from room notes even when the uncertainty report is capped", Show(cappedResult) + " " + string.Join(",", capped.Uncertainties.Select(u => u.Code)));
        }
        catch (Exception error) { C(false, "gen unexpected", error.GetType().Name + ": " + error.Message + " " + error.StackTrace); }
    }
}
