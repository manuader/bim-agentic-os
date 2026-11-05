namespace Bim.Core;

// P4-06 default dimension chains: one DocDimension per exterior wall line of a level, between the two walls meeting
// its ends. Vendor-free, BCL only, no BIM-IR or persisted format change. Contract: docs/contracts/module-contracts.md.
//
// Components: walls of the level are joined when an axis endpoint of one lies within Tolerance of the other's axis.
// Runs: walls whose two axis endpoints lie within Tolerance of a wall's axis line are collinear; collinear walls whose
// axis intervals touch or overlap (Tolerance) form one run, the wall line. Its key is "dim-wall-<N>" of its first wall
// by id ("D", ordinal), whose start -> end gives the line direction d.
// Exterior: for exactly one side, the ray perpendicular to the line from the axis midpoint of every wall of the run
// crosses no other wall of the run's component (beyond Tolerance); that side is outside. Walls of other components
// (another building, a free-standing site wall) are ignored; a run free on both sides or on neither is skipped.
// Ends: the extreme axis endpoints of the run. At each end the reference is the first wall by id of the component, not
// on the run, whose axis is perpendicular to the line (unit dot product within Tolerance) and has an endpoint within
// Tolerance of that end. First is the reference at the lower end along d. A run is skipped when an end has none or the
// two references are not parallel within DocRegeneration's 1e-9.
// Face: a BIM-IR wall is realized unflipped, so its Revit exterior face is on the left of start -> end (Revit 2025.2,
// docs/revit-playbook.md). The chain measures the faces bounding the line's outside: at a convex end (the end wall runs
// to the inside) the end wall's face pointing away from the run, at a reentrant end (it runs to the outside) the face
// pointing towards the run. WallFace is "exterior" or "interior", whichever gives that face at both ends; a run whose
// two ends need different faces is skipped (catalogue wall_face is one value for both references).
// Line: from the first reference's axis to the second's, ClearanceMetres outside the line. OffsetMetres is rounded to
// 1e-9 m to absorb floating-point noise, so a rigid translation of a room normally gives the same value (pinned by a
// test, not guaranteed for every input).
static class DocDefaultsDimensions
{
    public const double ClearanceMetres = 1.0;
    const double Tolerance = 1e-6;
    const double ParallelTolerance = 1e-9;

    static string K(Guid id) => id.ToString("N");
    static double Length(double[] g) => Math.Sqrt((g[2] - g[0]) * (g[2] - g[0]) + (g[3] - g[1]) * (g[3] - g[1]));

    static bool OnSegment(double px, double py, double[] g)
    {
        double rx = g[2] - g[0], ry = g[3] - g[1];
        var t = Math.Clamp(((px - g[0]) * rx + (py - g[1]) * ry) / (rx * rx + ry * ry), 0, 1);
        double cx = g[0] + rx * t - px, cy = g[1] + ry * t - py;
        return Math.Sqrt(cx * cx + cy * cy) <= Tolerance;
    }

    static bool Touch(double[] a, double[] b) =>
        OnSegment(a[0], a[1], b) || OnSegment(a[2], a[3], b) || OnSegment(b[0], b[1], a) || OnSegment(b[2], b[3], a);

    // Whether the ray from (px, py) along the unit (mx, my), beyond Tolerance, meets the axis of the wall g.
    static bool Hits(double px, double py, double mx, double my, double[] g)
    {
        double rx = g[2] - g[0], ry = g[3] - g[1], wx = g[0] - px, wy = g[1] - py, r = Math.Sqrt(rx * rx + ry * ry);
        var denominator = mx * ry - my * rx;
        if (Math.Abs(denominator) <= 1e-12 * r)
        {
            if (Math.Abs(wx * my - wy * mx) > Tolerance) return false;
            return Math.Max(wx * mx + wy * my, (g[2] - px) * mx + (g[3] - py) * my) > Tolerance;
        }
        var s = (wx * ry - wy * rx) / denominator;
        var v = (wx * my - wy * mx) / denominator;
        return s > Tolerance && v >= -Tolerance / r && v <= 1 + Tolerance / r;
    }

    // walls: the walls standing on one level, ordered by id ("D", ordinal), each with valid wall geometry.
    public static IEnumerable<DocDimension> Chains(string view, IReadOnlyList<Element> walls)
    {
        var usable = walls.Where(wall => Length(wall.GeometryMetres) > Tolerance).ToArray();
        var parent = Enumerable.Range(0, usable.Length).ToArray();
        int Root(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }
        for (var i = 0; i < usable.Length; i++)
            for (var j = i + 1; j < usable.Length; j++)
                if (Touch(usable[i].GeometryMetres, usable[j].GeometryMetres)) parent[Root(i)] = Root(j);
        var component = Enumerable.Range(0, usable.Length).Select(Root).ToArray();

        var assigned = new bool[usable.Length];
        var result = new List<DocDimension>();
        for (var i = 0; i < usable.Length; i++)
        {
            if (assigned[i]) continue;
            var g = usable[i].GeometryMetres;
            var length = Length(g);
            double lx = g[0], ly = g[1], ldx = (g[2] - g[0]) / length, ldy = (g[3] - g[1]) / length;
            double Across(double x, double y) => -(x - lx) * ldy + (y - ly) * ldx;
            double Along(double x, double y) => (x - lx) * ldx + (y - ly) * ldy;
            var collinear = Enumerable.Range(i, usable.Length - i).Where(candidate => !assigned[candidate]
                && Math.Abs(Across(usable[candidate].GeometryMetres[0], usable[candidate].GeometryMetres[1])) <= Tolerance
                && Math.Abs(Across(usable[candidate].GeometryMetres[2], usable[candidate].GeometryMetres[3])) <= Tolerance).ToArray();
            foreach (var member in collinear) assigned[member] = true;
            double Low(int index) => Math.Min(Along(usable[index].GeometryMetres[0], usable[index].GeometryMetres[1]), Along(usable[index].GeometryMetres[2], usable[index].GeometryMetres[3]));
            double High(int index) => Math.Max(Along(usable[index].GeometryMetres[0], usable[index].GeometryMetres[1]), Along(usable[index].GeometryMetres[2], usable[index].GeometryMetres[3]));
            var runs = new List<List<int>>();
            var reach = double.NegativeInfinity;
            foreach (var member in collinear.OrderBy(Low).ThenBy(index => index))
            {
                if (runs.Count == 0 || Low(member) > reach + Tolerance)
                {
                    runs.Add(new List<int>());
                    reach = High(member);
                }
                else reach = Math.Max(reach, High(member));
                runs[^1].Add(member);
            }
            foreach (var run in runs)
                if (Chain(view, usable, component, run.Order().ToArray()) is { } dimension) result.Add(dimension);
        }
        return result;
    }

    // run: indices into usable, ascending (so run[0] is the first wall by id).
    static DocDimension? Chain(string view, Element[] usable, int[] component, int[] run)
    {
        var key = usable[run[0]];
        var g = key.GeometryMetres;
        var length = Length(g);
        double ox = g[0], oy = g[1], dx = (g[2] - g[0]) / length, dy = (g[3] - g[1]) / length, nx = -dy, ny = dx;
        double Along(double x, double y) => (x - ox) * dx + (y - oy) * dy;
        var others = Enumerable.Range(0, usable.Length).Where(index => component[index] == component[run[0]] && !run.Contains(index)).ToArray();

        bool Free(double side) => run.All(member =>
        {
            var w = usable[member].GeometryMetres;
            double mx = (w[0] + w[2]) / 2, my = (w[1] + w[3]) / 2;
            return !others.Any(index => Hits(mx, my, nx * side, ny * side, usable[index].GeometryMetres));
        });
        bool plus = Free(1), minus = Free(-1);
        if (plus == minus) return null;
        double outX = plus ? nx : -nx, outY = plus ? ny : -ny;

        var along = run.SelectMany(member => new[] { Along(usable[member].GeometryMetres[0], usable[member].GeometryMetres[1]), Along(usable[member].GeometryMetres[2], usable[member].GeometryMetres[3]) }).ToArray();
        double t0 = along.Min(), t1 = along.Max();
        if (!(t1 - t0 > Tolerance)) return null;
        Element? EndWall(double x, double y) => others.Select(index => usable[index]).FirstOrDefault(other =>
        {
            var o = other.GeometryMetres;
            var l = Length(o);
            if (Math.Abs((o[2] - o[0]) / l * dx + (o[3] - o[1]) / l * dy) > Tolerance) return false;
            return (Math.Abs(o[0] - x) <= Tolerance && Math.Abs(o[1] - y) <= Tolerance) || (Math.Abs(o[2] - x) <= Tolerance && Math.Abs(o[3] - y) <= Tolerance);
        });
        double e0x = ox + dx * t0, e0y = oy + dy * t0, e1x = ox + dx * t1, e1y = oy + dy * t1;
        var first = EndWall(e0x, e0y);
        var second = EndWall(e1x, e1y);
        if (first is null || second is null || first.Id == second.Id) return null;
        var a = first.GeometryMetres;
        var b = second.GeometryMetres;
        double la = Length(a), lb = Length(b), ux = (a[2] - a[0]) / la, uy = (a[3] - a[1]) / la;
        if (Math.Abs(ux * ((b[3] - b[1]) / lb) - uy * ((b[2] - b[0]) / lb)) > ParallelTolerance) return null;

        // The face (true: Revit exterior, left of start -> end) bounding the line's outside at an end; away is the unit
        // pointing from the run past that end (-d at the first end, +d at the second).
        bool Exterior(double[] wall, double endX, double endY, double awayX, double awayY)
        {
            var l = Length(wall);
            double wx = (wall[2] - wall[0]) / l, wy = (wall[3] - wall[1]) / l;
            var far = Math.Abs(wall[0] - endX) <= Tolerance && Math.Abs(wall[1] - endY) <= Tolerance ? (X: wall[2], Y: wall[3]) : (X: wall[0], Y: wall[1]);
            var reentrant = (far.X - endX) * outX + (far.Y - endY) * outY > 0;
            double wantX = reentrant ? -awayX : awayX, wantY = reentrant ? -awayY : awayY;
            return -wy * wantX + wx * wantY > 0;
        }
        var firstExterior = Exterior(a, e0x, e0y, -dx, -dy);
        if (firstExterior != Exterior(b, e1x, e1y, dx, dy)) return null;

        double sx = e0x + outX * ClearanceMetres, sy = e0y + outY * ClearanceMetres;
        var offset = Math.Round((sx - (a[0] + a[2]) / 2) * ux + (sy - (a[1] + a[3]) / 2) * uy, 9, MidpointRounding.ToEven);
        if (offset == 0) offset = 0.0;
        return new DocDimension("dim-wall-" + K(key.Id), view, first.Id, second.Id, offset, firstExterior ? "exterior" : "interior");
    }
}
