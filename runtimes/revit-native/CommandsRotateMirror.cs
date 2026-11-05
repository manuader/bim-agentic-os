using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// element.rotate / element.mirror executor decisions, expected locations, readback checks and readback JSON, in
// millimetres (project coordinates). Autodesk-free on purpose: tests/revit-commands source-links this file.
// CommandsExecutorRotateMirror.cs reads the Revit facts (with the move/copy readers in CommandsExecutorTransform.cs)
// and calls the Revit API. Locations are the move/copy TransformLocation (CommandsTransform.cs); a bounding box does
// not turn with its element, so box-only elements are refused here.

// A turn about the vertical axis through (CenterX, CenterY) by AngleRad, counter-clockwise seen from above.
public sealed record PlanRotation(double CenterX, double CenterY, double AngleRad, double? AngleDeg = null);
// Red stub (P1-10 review): the new helpers below answer wrongly until implemented.
public sealed record PlanAxis(double[] BottomMm, double[] Direction);
public sealed record PlanPlane(double[] Normal, double[] OriginMm);
// A reflection in the vertical plane through the plan line (StartX, StartY) -> (EndX, EndY).
public sealed record PlanMirror(double StartX, double StartY, double EndX, double EndY);
// One mirrored source: UniqueId is its mirrored copy (keep_original true), the source itself mirrored in place, or the
// replacement Revit made for it (IsNew true).
public sealed record MirrorResult(string SourceUid, string UniqueId, bool IsNew);
// Results: one per source, in request order. AlsoCreated: new elements that are no source's result (hosted doors and
// windows Revit mirrored with their walls). Mismatch: why a source has no result, else null.
public sealed record MirrorMapping(IReadOnlyList<MirrorResult> Results, IReadOnlyList<string> AlsoCreated, string? Mismatch, IReadOnlyList<string>? Deleted = null);

public static class CommandRulesRotateMirror
{
    // Refusal order: pinned, view-specific, group member, hosted without host (the move/copy codes), then a hosted
    // element whose host is not a target, no location point / curve / sketch profile (element_not_rotatable).
    public static void CheckRotateTarget(TransformTargetFacts facts, IReadOnlyCollection<string> targetUids, PlanRotation rotation, string field)
    {
        CheckCommon(facts, field);
        if (Unsupported(facts, targetUids) is { } reason) throw new RevitCommandException(RevitRotateMirrorCodes.ElementNotRotatable, field, facts.Uid + " " + reason);
    }

    // As CheckRotateTarget with element_not_mirrorable, then Revit's own CanMirrorElements answer (mirrorable).
    public static void CheckMirrorTarget(TransformTargetFacts facts, bool mirrorable, IReadOnlyCollection<string> targetUids, PlanMirror mirror, bool keepOriginal, string field)
    {
        CheckCommon(facts, field);
        if (Unsupported(facts, targetUids) is { } reason) throw new RevitCommandException(RevitRotateMirrorCodes.ElementNotMirrorable, field, facts.Uid + " " + reason);
        if (!mirrorable) throw new RevitCommandException(RevitRotateMirrorCodes.ElementNotMirrorable, field, facts.Uid + " cannot be mirrored (Revit's CanMirrorElements refuses it)");
    }

    public static void CheckRotateTargets(IReadOnlyList<TransformTargetFacts> facts, PlanRotation rotation)
    {
        var uids = facts.Select(fact => fact.Uid).ToArray();
        for (var i = 0; i < facts.Count; i++) CheckRotateTarget(facts[i], uids, rotation, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]");
    }

    public static void CheckMirrorTargets(IReadOnlyList<TransformTargetFacts> facts, PlanMirror mirror, bool keepOriginal, Func<IReadOnlyList<string>, bool> canMirror)
    {
        var uids = facts.Select(fact => fact.Uid).ToArray();
        for (var i = 0; i < facts.Count; i++) CheckMirrorTarget(facts[i], canMirror(new[] { facts[i].Uid }), uids, mirror, keepOriginal, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]");
    }

    public static PlanAxis RotationAxis(PlanRotation rotation) => new(new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 });
    public static PlanPlane MirrorPlane(PlanMirror mirror) => new(new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 });
    public static IReadOnlyList<string> MirrorDeleted(MirrorMapping mapping) => Array.Empty<string>();

    static void CheckCommon(TransformTargetFacts facts, string field)
    {
        if (facts.Pinned) throw new RevitCommandException(RevitTransformCodes.ElementPinned, field, facts.Uid + " is pinned; unpin it in Revit first");
        if (facts.ViewSpecific) throw new RevitCommandException(RevitTransformCodes.ElementViewSpecific, field, facts.Uid + " is view-specific (annotation or detail); only model elements are rotated or mirrored");
        if (facts.Grouped) throw new RevitCommandException(RevitTransformCodes.ElementInGroup, field, facts.Uid + " is a group member; edit or ungroup the group in Revit first");
        if (facts.HostedPlacement && !facts.HostPresent) throw new RevitCommandException(RevitTransformCodes.HostMissing, field, facts.Uid + " is a hosted element whose host is missing");
    }

    static string? Unsupported(TransformTargetFacts facts, IReadOnlyCollection<string> targetUids)
    {
        if (facts.HostedPlacement && (facts.HostUid is null || !targetUids.Contains(facts.HostUid, StringComparer.OrdinalIgnoreCase)))
            return "is hosted by " + (facts.HostUid ?? "another element") + ", which is not a target: a hosted element turns only with its host; include the host";
        if (facts.Location is null) return "has no location point, location curve or sketch profile to turn and read back";
        if (facts.Location.Kind == TransformLocationKinds.Box) return "is read back only by its bounding box, which does not turn with the element";
        return null;
    }

    public static PlanRotation RotationOf(ElementRotateArgs args) => new(args.Center.X.Millimetres, args.Center.Y.Millimetres, args.Angle.Radians);

    public static PlanMirror MirrorOf(ElementMirrorArgs args) => new(args.AxisStart.X.Millimetres, args.AxisStart.Y.Millimetres, args.AxisEnd.X.Millimetres, args.AxisEnd.Y.Millimetres);

    // p' = c + R(angle) (p - c) in plan; z unchanged.
    public static double[] RotatePoint(double[] point, PlanRotation rotation)
    {
        double cos = Math.Cos(rotation.AngleRad), sin = Math.Sin(rotation.AngleRad);
        double dx = point[0] - rotation.CenterX, dy = point[1] - rotation.CenterY;
        return new[] { rotation.CenterX + cos * dx - sin * dy, rotation.CenterY + sin * dx + cos * dy, point[2] };
    }

    // With u the unit direction of the line and a its start: p' = a + 2 ((p - a) . u) u - (p - a) in plan; z unchanged.
    public static double[] MirrorPoint(double[] point, PlanMirror mirror)
    {
        double ux = mirror.EndX - mirror.StartX, uy = mirror.EndY - mirror.StartY;
        var length = Math.Sqrt(ux * ux + uy * uy);
        ux /= length;
        uy /= length;
        double vx = point[0] - mirror.StartX, vy = point[1] - mirror.StartY;
        var along = vx * ux + vy * uy;
        return new[] { mirror.StartX + 2 * along * ux - vx, mirror.StartY + 2 * along * uy - vy, point[2] };
    }

    // The location turned: every point turned, a point's own rotation grown by the angle.
    public static TransformLocation Rotate(TransformLocation location, PlanRotation rotation) =>
        location with
        {
            A = RotatePoint(location.A, rotation),
            B = location.B is null ? null : RotatePoint(location.B, rotation),
            Points = location.Points?.Select(point => RotatePoint(point, rotation)).ToArray(),
            RotationRad = location.RotationRad is { } angle ? angle + rotation.AngleRad : null,
        };

    // The location mirrored: every point mirrored; a mirrored instance is a reflection, so no rotation is expected.
    public static TransformLocation Mirror(TransformLocation location, PlanMirror mirror) =>
        location with
        {
            A = MirrorPoint(location.A, mirror),
            B = location.B is null ? null : MirrorPoint(location.B, mirror),
            Points = location.Points?.Select(point => MirrorPoint(point, mirror)).ToArray(),
            RotationRad = null,
        };

    static double Distance(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    internal static string Format(double[] point) =>
        "(" + string.Join(", ", point.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))) + ")";

    // The largest distance of a mirrored location from where it is expected, or null when it is not within 1 mm:
    // a point by position, a curve with its ends either way round (Revit may reverse a mirrored curve), a sketch
    // profile as a set of points in any order (each expected point takes the nearest observed point not yet taken).
    static double? MirroredDeviation(TransformLocation expected, TransformLocation observed)
    {
        if (observed.Kind != expected.Kind) return null;
        var tolerance = CommandRulesTransform.ToleranceMm;
        switch (expected.Kind)
        {
            case TransformLocationKinds.Point:
            {
                var d = Distance(expected.A, observed.A);
                return d <= tolerance ? d : null;
            }
            case TransformLocationKinds.Curve:
            {
                if (expected.B is null || observed.B is null) return null;
                var same = Math.Max(Distance(expected.A, observed.A), Distance(expected.B, observed.B));
                var reversed = Math.Max(Distance(expected.A, observed.B), Distance(expected.B, observed.A));
                var d = Math.Min(same, reversed);
                return d <= tolerance ? d : null;
            }
            case TransformLocationKinds.Profile:
            {
                var wanted = expected.Points ?? Array.Empty<double[]>();
                var seen = observed.Points ?? Array.Empty<double[]>();
                if (wanted.Count != seen.Count) return null;
                var taken = new bool[seen.Count];
                var worst = 0.0;
                foreach (var point in wanted)
                {
                    var best = -1;
                    var bestDistance = double.PositiveInfinity;
                    for (var i = 0; i < seen.Count; i++)
                    {
                        if (taken[i]) continue;
                        var d = Distance(point, seen[i]);
                        if (d < bestDistance) { best = i; bestDistance = d; }
                    }
                    if (best < 0 || !(bestDistance <= tolerance)) return null;
                    taken[best] = true;
                    worst = Math.Max(worst, bestDistance);
                }
                return worst;
            }
            default:
                return null;
        }
    }

    // Null when observed is the expected mirrored location within 1 mm, else what differs.
    public static string? CompareMirrored(TransformLocation expected, TransformLocation? observed, string what)
    {
        if (observed is null) return what + " no longer has a readable location";
        if (observed.Kind != expected.Kind) return what + " has a location " + observed.Kind + " where a location " + expected.Kind + " was read before";
        if (MirroredDeviation(expected, observed) is not null) return null;
        return expected.Kind switch
        {
            TransformLocationKinds.Point => what + " location point is at " + Format(observed.A) + " mm, expected its mirror image " + Format(expected.A) + " within 1 mm",
            TransformLocationKinds.Curve => what + " location curve runs " + Format(observed.A) + " -> " + (observed.B is null ? "nothing" : Format(observed.B)) + " mm, expected its mirror image " + Format(expected.A) + " -> " + (expected.B is null ? "nothing" : Format(expected.B)) + " (either way) within 1 mm",
            TransformLocationKinds.Profile => what + " sketch profile (" + (observed.Points?.Count ?? 0) + " curve end points) is not the mirror image of the one read before (" + (expected.Points?.Count ?? 0) + " points, any order, each within 1 mm)",
            _ => what + " has a location " + expected.Kind + " that cannot be compared after a mirror",
        };
    }

    // Maps each source to its mirrored result. keepOriginal: every source must be unchanged (CommandRulesTransform.
    // Compare against what was read before) and gets a mirrored copy among created. Otherwise a source still present
    // (sourcesAfter[i] not null) must itself be at its mirror image; a source gone (null) gets the replacement Revit made
    // among created. Created elements carrying a source's own UniqueId are not new. Copies and replacements are paired
    // with the same key, nearest first then by augmenting paths (as element.copy); created elements nobody takes are
    // AlsoCreated, in Revit's order.
    public static MirrorMapping MapMirrored(IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformCandidate?> sourcesAfter, IReadOnlyList<TransformCandidate> created, PlanMirror mirror, bool keepOriginal)
    {
        static MirrorMapping Fail(string why) => new(Array.Empty<MirrorResult>(), Array.Empty<string>(), why);
        var sourceUids = new HashSet<string>(sources.Select(source => source.Uid), StringComparer.OrdinalIgnoreCase);
        var news = created.Where(candidate => !sourceUids.Contains(candidate.Uid)).ToArray();
        var expected = new TransformLocation[sources.Count];
        var results = new MirrorResult?[sources.Count];
        var pending = new List<int>();
        for (var s = 0; s < sources.Count; s++)
        {
            if (sources[s].Location is not { } before) return Fail("source " + sources[s].Uid + " has no location to map its mirror image by");
            expected[s] = Mirror(before, mirror);
            var after = s < sourcesAfter.Count ? sourcesAfter[s] : null;
            if (keepOriginal)
            {
                if (after is null) return Fail("source " + sources[s].Uid + " is gone; with keep_original true it must stay");
                if (CommandRulesTransform.Compare(before, after.Location, "source " + sources[s].Uid) is { } moved) return Fail(moved + "; with keep_original true it must stay");
                pending.Add(s);
            }
            else if (after is not null)
            {
                if (CompareMirrored(expected[s], after.Location, "element " + sources[s].Uid) is { } wrong) return Fail(wrong);
                results[s] = new MirrorResult(sources[s].Uid, sources[s].Uid, false);
            }
            else pending.Add(s);
        }
        // Admissible (pending source, new element) pairs ranked by deviation, ties by source then element order.
        var ranked = new List<(double Deviation, int Source, int Copy)>();
        foreach (var s in pending)
            for (var c = 0; c < news.Length; c++)
                if (string.Equals(news[c].Key, sources[s].Key, StringComparison.Ordinal) && news[c].Location is { } observed && MirroredDeviation(expected[s], observed) is { } deviation)
                    ranked.Add((deviation, s, c));
        var order = ranked.OrderBy(pair => pair.Deviation).ThenBy(pair => pair.Source).ThenBy(pair => pair.Copy).ToArray();
        var copyOf = Enumerable.Repeat(-1, sources.Count).ToArray();
        var sourceOf = Enumerable.Repeat(-1, news.Length).ToArray();
        foreach (var (_, source, copy) in order)
        {
            if (copyOf[source] >= 0 || sourceOf[copy] >= 0) continue;
            copyOf[source] = copy;
            sourceOf[copy] = source;
        }
        var options = sources.Select((_, s) => order.Where(pair => pair.Source == s).Select(pair => pair.Copy).ToArray()).ToArray();
        bool Augment(int source, bool[] visited)
        {
            foreach (var copy in options[source])
            {
                if (visited[copy]) continue;
                visited[copy] = true;
                if (sourceOf[copy] < 0 || Augment(sourceOf[copy], visited))
                {
                    copyOf[source] = copy;
                    sourceOf[copy] = source;
                    return true;
                }
            }
            return false;
        }
        foreach (var s in pending)
            if (copyOf[s] < 0) Augment(s, new bool[news.Length]);
        foreach (var s in pending)
        {
            if (copyOf[s] < 0)
                return Fail("no new element of the same kind sits at the mirror image of " + sources[s].Uid + " in the line " + Format(new[] { mirror.StartX, mirror.StartY, 0 }) + " -> " + Format(new[] { mirror.EndX, mirror.EndY, 0 }) + " mm (within 1 mm)"
                    + (keepOriginal ? "" : "; it is gone and nothing replaced it"));
            results[s] = new MirrorResult(sources[s].Uid, news[copyOf[s]].Uid, true);
        }
        return new MirrorMapping(results.Select(result => result!).ToArray(), news.Where((_, c) => sourceOf[c] < 0).Select(candidate => candidate.Uid).ToArray(), null);
    }

    // The reply's created list: the new results (copies or replacements) in request order, then the extra elements.
    public static IReadOnlyList<string> MirrorCreated(MirrorMapping mapping) => mapping.Results.Where(result => result.IsNew).Select(result => result.UniqueId).Concat(mapping.AlsoCreated).ToArray();

    // The reply's modified list: the originals mirrored in place, in request order.
    public static IReadOnlyList<string> MirrorModified(MirrorMapping mapping) => mapping.Results.Where(result => !result.IsNew).Select(result => result.UniqueId).ToArray();
}

public static class CommandReadbackRotateMirror
{
    // Every target's location (read before and after, from the element) turned by the rotation: in order (a turn is
    // rigid) with a point's rotation grown by the angle (CommandRulesTransform.Compare: 1 mm, 1e-6 rad).
    public static string? Rotated(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, PlanRotation rotation)
    {
        for (var i = 0; i < uids.Count; i++)
            if (CommandRulesTransform.Compare(CommandRulesRotateMirror.Rotate(before[i], rotation), after[i], "element " + uids[i]) is { } mismatch) return mismatch;
        return null;
    }

    public static string RotateJson(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, PlanRotation rotation) => Write(writer =>
    {
        Numbers(writer, "center_mm", new[] { rotation.CenterX, rotation.CenterY });
        writer.WriteNumber("angle_deg", rotation.AngleRad * 180 / Math.PI);
        writer.WriteStartArray("rotated");
        for (var i = 0; i < uids.Count; i++)
        {
            writer.WriteStartObject();
            writer.WriteString("unique_id", uids[i]);
            Location(writer, "before", before[i]);
            Location(writer, "after", after[i]);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    public static string MirrorJson(MirrorMapping mapping, IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformCandidate?> sourcesAfter, IReadOnlyList<TransformCandidate> created, PlanMirror mirror, bool keepOriginal) => Write(writer =>
    {
        writer.WriteStartObject("axis");
        Numbers(writer, "start", new[] { mirror.StartX, mirror.StartY });
        Numbers(writer, "end", new[] { mirror.EndX, mirror.EndY });
        writer.WriteEndObject();
        writer.WriteBoolean("keep_original", keepOriginal);
        writer.WriteStartArray("mirrored");
        foreach (var result in mapping.Results)
        {
            var index = -1;
            for (var i = 0; i < sources.Count; i++)
                if (string.Equals(sources[i].Uid, result.SourceUid, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            var after = result.IsNew
                ? created.FirstOrDefault(candidate => string.Equals(candidate.Uid, result.UniqueId, StringComparison.OrdinalIgnoreCase))?.Location
                : index >= 0 && index < sourcesAfter.Count ? sourcesAfter[index]?.Location : null;
            writer.WriteStartObject();
            writer.WriteString("source_unique_id", result.SourceUid);
            writer.WriteString("unique_id", result.UniqueId);
            writer.WriteBoolean("new", result.IsNew);
            Location(writer, "before", index >= 0 ? sources[index].Location : null);
            Location(writer, "after", after);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("also_created");
        foreach (var uid in mapping.AlsoCreated) writer.WriteStringValue(uid);
        writer.WriteEndArray();
    });

    static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Same shape as the element.move readback (CommandsTransform.cs): point (+ rotation_deg), curve start/end, profile
    // points, box min/max.
    static void Location(Utf8JsonWriter writer, string name, TransformLocation? location)
    {
        if (location is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name);
        writer.WriteString("kind", location.Kind);
        switch (location.Kind)
        {
            case TransformLocationKinds.Point:
                Numbers(writer, "point", location.A);
                if (location.RotationRad is { } rotation) writer.WriteNumber("rotation_deg", rotation * 180 / Math.PI);
                break;
            case TransformLocationKinds.Curve:
                Numbers(writer, "start", location.A);
                if (location.B is not null) Numbers(writer, "end", location.B);
                break;
            case TransformLocationKinds.Profile:
                writer.WriteStartArray("points");
                foreach (var point in location.Points ?? Array.Empty<double[]>())
                {
                    writer.WriteStartArray();
                    foreach (var value in point) writer.WriteNumberValue(value);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            default:
                Numbers(writer, "min", location.A);
                if (location.B is not null) Numbers(writer, "max", location.B);
                break;
        }
        writer.WriteEndObject();
    }

    static void Numbers(Utf8JsonWriter writer, string name, double[] values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }
}
