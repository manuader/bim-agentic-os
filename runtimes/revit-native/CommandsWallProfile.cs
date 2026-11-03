using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// wall.set_profile executor decisions, readback checks and readback JSON, in millimetres in the wall's vertical plane
// (u along the location line from its start, z above the base level's project elevation). Autodesk-free on purpose:
// tests/revit-commands source-links this file. CommandsExecutorWallProfile.cs reads the Revit facts (lengths
// converted with UnitUtils) and calls the Revit API.

public sealed record ProfilePointMm(double U, double Z);
// One sketch curve of the wall profile, its ends in u/z; Straight is false for anything but a Line.
public sealed record ProfileEdgeMm(ProfilePointMm A, ProfilePointMm B, bool Straight);
// A door, window or opening hosted by the wall (Wall.FindInserts), as the u/z extents of its bounding box.
public sealed record WallInsertExtent(string UniqueId, string Kind, double UMin, double UMax, double ZMin, double ZMax);
// What Revit reports after the edit. SketchUid: the wall's profile Sketch (null without one); Edges: every curve of
// Sketch.Profile; LoopCount: the number of curve loops; Start/End: the location line; TopMm: bounding box max z above
// the base level (null without a box); BaseOffsetMm/HeightMm: WALL_BASE_OFFSET / WALL_USER_HEIGHT_PARAM.
public sealed record ObservedWallProfile(string UniqueId, string? SketchUid, IReadOnlyList<ProfileEdgeMm> Edges, int LoopCount, PlanPoint Start, PlanPoint End, double? TopMm, double? BaseOffsetMm, double? HeightMm);

public static class CommandRulesWallProfile
{
    // Profile vertices are compared within 1 mm (sketch curves are exact; 1 mm absorbs unit conversion and Revit's
    // snapping); bounding boxes with 25 mm because Revit pads element boxes (as for toposolids).
    public const double ProfileToleranceMm = 1.0;
    public const double BoxToleranceMm = 25.0;

    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static ProfilePointMm[] Requested(WallSetProfileArgs args) => args.Profile.Select(v => new ProfilePointMm(v.U.Millimetres, v.Z.Millimetres)).ToArray();

    // wallKind: WallType.Kind name (Basic, Curtain, Stacked). Only straight basic walls Revit allows a profile sketch on.
    public static void CheckWall(string wallKind, bool isArc, bool canHaveProfileSketch)
    {
        if (isArc) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.wall_uid", "the wall is curved; wall.set_profile edits straight walls only");
        if (wallKind != "Basic") throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.wall_uid", "the wall is a " + wallKind.ToLowerInvariant() + " wall; wall.set_profile edits straight basic walls only");
        if (!canHaveProfileSketch) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.wall_uid", "Revit does not allow an elevation profile on this wall (Wall.CanHaveProfileSketch is false; for example an attached or in-place wall)");
    }

    // The profile must span u 0 to the wall's length so the location line stays where it is.
    public static void CheckSpan(IReadOnlyList<ProfilePointMm> profile, double wallLengthMm)
    {
        var min = profile.Min(p => p.U);
        var max = profile.Max(p => p.U);
        if (Math.Abs(min) > ProfileToleranceMm || Math.Abs(max - wallLengthMm) > ProfileToleranceMm)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.profile",
                "the profile spans u " + F(min) + " to " + F(max) + " mm; it must span 0 to the wall length " + F(wallLengthMm) + " mm (within 1 mm) so the location line is kept");
    }

    // Every hosted opening must stay inside the new profile; the first one that would not is named.
    public static void CheckInserts(IReadOnlyList<ProfilePointMm> profile, IReadOnlyList<WallInsertExtent> inserts)
    {
        foreach (var insert in inserts)
        {
            if (!Contains(profile, insert))
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.profile",
                    "the wall hosts " + insert.Kind + " " + insert.UniqueId + " (u " + F(insert.UMin) + " to " + F(insert.UMax) + ", z " + F(insert.ZMin) + " to " + F(insert.ZMax)
                    + " mm), which would fall outside the new profile; move or delete it first, or change the profile");
        }
    }

    // The opening's rectangle, shrunk by 1 mm, lies inside the polygon: no polygon edge enters its interior and its
    // centre is inside. (A simple polygon's boundary that does not cross the rectangle leaves it wholly in or out.)
    public static bool Contains(IReadOnlyList<ProfilePointMm> profile, WallInsertExtent insert)
    {
        var shrinkU = Math.Min(ProfileToleranceMm, (insert.UMax - insert.UMin) / 4);
        var shrinkZ = Math.Min(ProfileToleranceMm, (insert.ZMax - insert.ZMin) / 4);
        double u0 = insert.UMin + shrinkU, u1 = insert.UMax - shrinkU, z0 = insert.ZMin + shrinkZ, z1 = insert.ZMax - shrinkZ;
        for (var i = 0; i < profile.Count; i++)
        {
            var a = profile[i];
            var b = profile[(i + 1) % profile.Count];
            if (SegmentEntersBox(a, b, u0, u1, z0, z1)) return false;
        }
        return Inside(profile, (u0 + u1) / 2, (z0 + z1) / 2);
    }

    // Liang-Barsky clipping against the open box: some part of the segment strictly inside it.
    static bool SegmentEntersBox(ProfilePointMm a, ProfilePointMm b, double u0, double u1, double z0, double z1)
    {
        double t0 = 0, t1 = 1, du = b.U - a.U, dz = b.Z - a.Z;
        bool Clip(double p, double q)
        {
            if (p == 0) return q > 0;
            var r = q / p;
            if (p < 0) { if (r >= t1) return false; if (r > t0) t0 = r; }
            else { if (r <= t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        return Clip(-du, a.U - u0) && Clip(du, u1 - a.U) && Clip(-dz, a.Z - z0) && Clip(dz, z1 - a.Z) && t1 - t0 > 1e-12;
    }

    // Even-odd ray cast.
    static bool Inside(IReadOnlyList<ProfilePointMm> polygon, double u, double z)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Z > z) != (b.Z > z) && u < (b.U - a.U) * (z - a.Z) / (b.Z - a.Z) + a.U) inside = !inside;
        }
        return inside;
    }

    static double Distance(ProfilePointMm a, ProfilePointMm b) => Math.Sqrt((a.U - b.U) * (a.U - b.U) + (a.Z - b.Z) * (a.Z - b.Z));

    // Chains the edges of one loop into its vertex cycle, whatever their order and direction; null when the edges do
    // not form exactly one closed loop (ends meeting within 1 mm).
    public static ProfilePointMm[]? Chain(IReadOnlyList<ProfileEdgeMm> edges)
    {
        if (edges.Count < 3) return null;
        var remaining = edges.ToList();
        var first = remaining[0];
        remaining.RemoveAt(0);
        var loop = new List<ProfilePointMm> { first.A };
        var current = first.B;
        while (remaining.Count > 0)
        {
            var index = remaining.FindIndex(edge => Distance(edge.A, current) <= ProfileToleranceMm || Distance(edge.B, current) <= ProfileToleranceMm);
            if (index < 0) return null;
            var next = remaining[index];
            remaining.RemoveAt(index);
            loop.Add(current);
            current = Distance(next.A, current) <= ProfileToleranceMm ? next.B : next.A;
        }
        return Distance(current, first.A) <= ProfileToleranceMm ? loop.ToArray() : null;
    }

    // Drops a vertex that lies within 1 mm of the segment between its neighbours (Revit may split or merge collinear
    // sketch lines), repeating until nothing changes.
    public static ProfilePointMm[] Simplify(IReadOnlyList<ProfilePointMm> loop)
    {
        var points = loop.ToList();
        var changed = true;
        while (changed && points.Count > 3)
        {
            changed = false;
            for (var i = 0; i < points.Count && points.Count > 3; i++)
            {
                var prev = points[(i + points.Count - 1) % points.Count];
                var next = points[(i + 1) % points.Count];
                if (PointSegmentDistance(points[i], prev, next) <= ProfileToleranceMm) { points.RemoveAt(i); changed = true; break; }
            }
        }
        return points.ToArray();
    }

    static double PointSegmentDistance(ProfilePointMm p, ProfilePointMm a, ProfilePointMm b)
    {
        double du = b.U - a.U, dz = b.Z - a.Z, lengthSquared = du * du + dz * dz;
        var t = lengthSquared > 0 ? Math.Clamp(((p.U - a.U) * du + (p.Z - a.Z) * dz) / lengthSquared, 0, 1) : 0;
        return Distance(p, new ProfilePointMm(a.U + t * du, a.Z + t * dz));
    }

    // Same cycle of vertices within 1 mm, whatever the start vertex and orientation.
    public static bool SameLoop(IReadOnlyList<ProfilePointMm> a, IReadOnlyList<ProfilePointMm> b)
    {
        if (a.Count != b.Count || a.Count == 0) return false;
        var n = a.Count;
        for (var shift = 0; shift < n; shift++)
        {
            bool forward = true, backward = true;
            for (var i = 0; i < n && (forward || backward); i++)
            {
                if (Distance(a[i], b[(shift + i) % n]) > ProfileToleranceMm) forward = false;
                if (Distance(a[i], b[((shift - i) % n + n) % n]) > ProfileToleranceMm) backward = false;
            }
            if (forward || backward) return true;
        }
        return false;
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackWallProfile
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string Loop(IReadOnlyList<ProfilePointMm> loop) => "[" + string.Join(", ", loop.Select(p => "[" + F(Math.Round(p.U, 1)) + ", " + F(Math.Round(p.Z, 1)) + "]")) + "]";
    static bool Near(PlanPoint a, PlanPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) <= CommandRulesWallProfile.ProfileToleranceMm;

    // requested: the profile vertices; start/end: the location line before the edit.
    public static string? Profile(IReadOnlyList<ProfilePointMm> requested, PlanPoint start, PlanPoint end, ObservedWallProfile observed)
    {
        if (observed.SketchUid is null) return "the wall has no profile sketch after the edit";
        if (observed.LoopCount != 1) return "the profile sketch has " + observed.LoopCount + " loops, expected one closed loop";
        if (observed.Edges.Any(edge => !edge.Straight)) return "the profile sketch has an edge that is not a straight line";
        if (CommandRulesWallProfile.Chain(observed.Edges) is not { } loop) return "the profile sketch edges do not form one closed loop";
        var actual = CommandRulesWallProfile.Simplify(loop);
        var expected = CommandRulesWallProfile.Simplify(requested);
        if (!CommandRulesWallProfile.SameLoop(expected, actual)) return "the wall profile reads back as " + Loop(actual) + " (u, z mm), expected " + Loop(expected) + " within 1 mm";
        if (!Near(observed.Start, start) || !Near(observed.End, end))
            return "the wall location line moved to (" + F(observed.Start.X) + ", " + F(observed.Start.Y) + ") - (" + F(observed.End.X) + ", " + F(observed.End.Y) + "), expected (" + F(start.X) + ", " + F(start.Y) + ") - (" + F(end.X) + ", " + F(end.Y) + ")";
        var top = requested.Max(p => p.Z);
        if (observed.TopMm is not { } box) return "the wall has no bounding box";
        if (Math.Abs(box - top) > CommandRulesWallProfile.BoxToleranceMm) return "the wall bounding box top is " + F(box) + " mm above the base level, expected the profile's highest z " + F(top) + " mm";
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres) ----

    public static string ProfileJson(ObservedWallProfile observed)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("wall_profile");
            writer.WriteString("unique_id", observed.UniqueId);
            if (observed.SketchUid is null) writer.WriteNull("sketch_unique_id"); else writer.WriteString("sketch_unique_id", observed.SketchUid);
            writer.WriteNumber("loop_count", observed.LoopCount);
            if (observed.SketchUid is not null && CommandRulesWallProfile.Chain(observed.Edges) is { } loop)
            {
                writer.WriteStartArray("profile_mm");
                foreach (var point in CommandRulesWallProfile.Simplify(loop))
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(point.U);
                    writer.WriteNumberValue(point.Z);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            else writer.WriteNull("profile_mm");
            Number(writer, "top_mm", observed.TopMm);
            Number(writer, "base_offset_mm", observed.BaseOffsetMm);
            Number(writer, "unconnected_height_mm", observed.HeightMm);
            Pair(writer, "location_start_mm", observed.Start);
            Pair(writer, "location_end_mm", observed.End);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    static void Pair(Utf8JsonWriter writer, string name, PlanPoint point)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteEndArray();
    }
}
