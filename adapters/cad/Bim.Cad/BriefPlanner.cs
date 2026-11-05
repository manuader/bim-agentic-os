using Bim.Core;
namespace Bim.Cad;

// P6-08: turns a P6-05 brief draft (BriefExtract) into a validated P6-03 planner brief for the P7-01 space planner.
// Pure and deterministic: no I/O, no clock, no culture dependence; the draft is not modified.
// 1. Each brief room is matched to its draft proposal by id. A brief room without a proposal has no notes: its
//    confidence is its provenance confidence (1 without provenance), so it can only be low-confidence.
// 2. Label groups: rooms whose names have the same extractor slug (BriefExtract.Slug, the draft id stem), joined with
//    the room a duplicate-label uncertainty names when that uncertainty's first source is another room's label (so a
//    hand-built or capped report still groups correctly).
// 3. Reasons, in draft order: low-confidence when the confidence is below MinConfidence; area-mismatch when the
//    proposal carries the area-mismatch note; duplicate-label when the room carries the duplicate-label note or is
//    named by a duplicate-label uncertainty, and an earlier room of its group was kept. A copy whose earlier copies
//    were all dropped is thus promoted to the group's first surviving room.
// 4. A room is kept when each reason is confirmed by room id or by reason code; otherwise it is dropped and listed with
//    its unconfirmed reasons. Adjacencies naming a dropped room are removed.
// 5. The brief is checked with BriefValidator (dropping every room gives rooms_empty; caller name and constraints are
//    checked).
public static class BriefPlanner
{
    public static BriefPlannerResult ToPlannerBrief(BriefDraft draft, BriefPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        options ??= new BriefPlannerOptions();
        var threshold = options.MinConfidence;
        if (!(threshold >= 0 && threshold <= 1)) throw new ArgumentOutOfRangeException(nameof(options), threshold, "MinConfidence must be within 0..1");
        var confirmedRooms = new HashSet<string>(options.ConfirmedRooms ?? Array.Empty<string>(), StringComparer.Ordinal);
        var confirmedReasons = new HashSet<string>(options.ConfirmedReasons ?? Array.Empty<string>(), StringComparer.Ordinal);
        confirmedRooms.Remove(null!);
        confirmedReasons.Remove(null!);

        var proposals = new Dictionary<string, BriefRoomProposal>(StringComparer.Ordinal);
        foreach (var proposal in draft.Rooms) proposals.TryAdd(proposal.Id, proposal);
        var draftBrief = draft.Brief;
        var group = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var room in draftBrief.Rooms) group.TryAdd(room.Id, "slug:" + BriefExtract.Slug(room.Name));
        var reportedDuplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uncertainty in draft.Uncertainties)
        {
            if (uncertainty.Code != BriefPlannerCodes.DuplicateLabel || uncertainty.RoomId is not { } duplicateId) continue;
            reportedDuplicates.Add(duplicateId);
            if (uncertainty.Sources.Count == 0) continue;
            var first = draft.Rooms.FirstOrDefault(proposal => proposal.Id != duplicateId && proposal.Label == uncertainty.Sources[0]);
            if (first is not null && group.TryGetValue(first.Id, out var firstGroup) && group.ContainsKey(duplicateId))
            {
                var old = group[duplicateId];
                foreach (var id in group.Keys.ToArray()) if (group[id] == old) group[id] = firstGroup;
            }
        }

        var keptGroups = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<BriefPlannerRoom>();
        var dropped = new List<BriefPlannerRoom>();
        var rooms = new List<BriefRoom>();
        var keptIds = new HashSet<string>(StringComparer.Ordinal);
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in draftBrief.Rooms)
        {
            knownIds.Add(room.Id);
            proposals.TryGetValue(room.Id, out var proposal);
            var roomGroup = group[room.Id];
            var reasons = new List<string>();
            var confidence = proposal?.Confidence ?? room.Provenance?.Confidence ?? 1;
            if (proposal is not null && proposal.Notes.Contains(BriefPlannerCodes.AreaMismatch, StringComparer.Ordinal)) reasons.Add(BriefPlannerCodes.AreaMismatch);
            var noted = (proposal is not null && proposal.Notes.Contains(BriefPlannerCodes.DuplicateLabel, StringComparer.Ordinal)) || reportedDuplicates.Contains(room.Id);
            if (noted && keptGroups.Contains(roomGroup)) reasons.Add(BriefPlannerCodes.DuplicateLabel);
            if (!(confidence >= threshold)) reasons.Add(BriefPlannerCodes.LowConfidence);
            reasons.Sort(StringComparer.Ordinal);

            var byRoom = confirmedRooms.Contains(room.Id);
            var confirmed = reasons.Where(reason => byRoom || confirmedReasons.Contains(reason)).ToArray();
            var open = reasons.Where(reason => !(byRoom || confirmedReasons.Contains(reason))).ToArray();
            var decision = new BriefPlannerRoom(room.Id, room.Name, confidence, reasons.ToArray(), confirmed, open);
            if (decision.IsKept)
            {
                kept.Add(decision);
                rooms.Add(room);
                keptIds.Add(room.Id);
                keptGroups.Add(roomGroup);
            }
            else dropped.Add(decision);
        }

        var adjacencies = draftBrief.Adjacencies.Where(adjacency => keptIds.Contains(adjacency.A) && keptIds.Contains(adjacency.B)).ToArray();
        var brief = new ProjectBrief(draftBrief.Version, options.Name ?? draftBrief.Name, options.Constraints ?? draftBrief.Constraints, rooms.ToArray(), adjacencies);
        var unknownRooms = confirmedRooms.Where(id => !knownIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var unknownReasons = confirmedReasons.Where(code => !BriefPlannerCodes.All.Contains(code, StringComparer.Ordinal)).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        return new BriefPlannerResult(brief, kept.ToArray(), dropped.ToArray(), unknownRooms, unknownReasons, BriefValidator.Validate(brief));
    }
}
