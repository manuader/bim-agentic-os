using System.Globalization;
using System.Text;
using Bim.Core;
namespace Bim.Cad;

// P6-08: turns a P6-05 brief draft (BriefExtract) into a validated P6-03 planner brief for the P7-01 space planner.
// Pure and deterministic: no I/O, no clock, no culture dependence; the draft is not modified.
// 1. Each brief room is matched to its draft proposal by id (a brief room without a proposal has no reasons).
// 2. Reasons: low-confidence when the proposal confidence is below MinConfidence; area-mismatch when the proposal
//    carries the area-mismatch note; duplicate-label when it carries the duplicate-label note and is a later copy of
//    its label: named by a duplicate-label uncertainty, or an earlier room with that note has the same label key
//    (lowercase, accents removed, other characters to '-', as the draft id slugs), so a capped uncertainty report
//    still finds them. The first room of a repeated label keeps its place.
// 3. A room is kept when each reason is confirmed by room id or by reason code; otherwise it is dropped and listed with
//    its unconfirmed reasons. Adjacencies naming a dropped room are removed.
// 4. The brief is checked with BriefValidator (dropping every room gives rooms_empty; caller constraints are checked).
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
        var reportedDuplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uncertainty in draft.Uncertainties)
            if (uncertainty.Code == BriefPlannerCodes.DuplicateLabel && uncertainty.RoomId is { } duplicateId) reportedDuplicates.Add(duplicateId);

        var draftBrief = draft.Brief;
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<BriefPlannerRoom>();
        var dropped = new List<BriefPlannerRoom>();
        var rooms = new List<BriefRoom>();
        var keptIds = new HashSet<string>(StringComparer.Ordinal);
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in draftBrief.Rooms)
        {
            knownIds.Add(room.Id);
            proposals.TryGetValue(room.Id, out var proposal);
            var reasons = new List<string>();
            var confidence = proposal?.Confidence ?? room.Provenance?.Confidence ?? 1;
            if (proposal is not null)
            {
                if (proposal.Notes.Contains(BriefPlannerCodes.AreaMismatch, StringComparer.Ordinal)) reasons.Add(BriefPlannerCodes.AreaMismatch);
                if (proposal.Notes.Contains(BriefPlannerCodes.DuplicateLabel, StringComparer.Ordinal))
                {
                    var key = LabelKey(proposal.Name);
                    var later = !seenLabels.Add(key);
                    if (later || reportedDuplicates.Contains(proposal.Id)) reasons.Add(BriefPlannerCodes.DuplicateLabel);
                }
            }
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
            }
            else dropped.Add(decision);
        }

        var adjacencies = draftBrief.Adjacencies.Where(adjacency => keptIds.Contains(adjacency.A) && keptIds.Contains(adjacency.B)).ToArray();
        var brief = new ProjectBrief(draftBrief.Version, options.Name ?? draftBrief.Name, options.Constraints ?? draftBrief.Constraints, rooms.ToArray(), adjacencies);
        var unknownRooms = confirmedRooms.Where(id => !knownIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var unknownReasons = confirmedReasons.Where(code => !BriefPlannerCodes.All.Contains(code, StringComparer.Ordinal)).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        return new BriefPlannerResult(brief, kept.ToArray(), dropped.ToArray(), unknownRooms, unknownReasons, BriefValidator.Validate(brief));
    }

    // Label key: lowercase invariant, accents removed, runs of other characters to one '-', trimmed.
    private static string LabelKey(string name)
    {
        var builder = new StringBuilder(name.Length);
        var dash = false;
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                if (dash && builder.Length > 0) builder.Append('-');
                dash = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else dash = true;
        }
        return builder.ToString();
    }
}
