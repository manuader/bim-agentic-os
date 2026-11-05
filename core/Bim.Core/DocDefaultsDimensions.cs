namespace Bim.Core;

// P4-06 default dimension chains: one DocDimension per exterior wall line of a level, between the two walls meeting
// its ends. Vendor-free, BCL only, no BIM-IR or persisted format change. Contract: docs/contracts/module-contracts.md.
//
// A wall line is a wall plus every other wall of the level collinear with it (both axis endpoints within Tolerance of
// its axis line); its key is "dim-wall-<N>" of its first wall by id ("D", ordinal). It is exterior when every other
// wall endpoint of the level lies on one side of it (beyond Tolerance), and that side is inside. Its ends are the
// extreme axis endpoints of its walls; the end references are, at each end, the first wall by id not on the line
// whose axis is perpendicular to the line (Tolerance on the unit dot product) and has an endpoint within Tolerance of
// that end. A line is skipped when an end has no such wall or the two end walls are not parallel within
// DocRegeneration's tolerance (dimension.create_linear measures between parallel walls). The dimension line runs from
// the first end wall's axis to the second's, ClearanceMetres outside the line (OffsetMetres rounded to 1e-9 m, so a
// rigid translation of the level keeps the set byte-identical).
static class DocDefaultsDimensions
{
    public const double ClearanceMetres = 1.0;
    const double Tolerance = 1e-6;
    const double ParallelTolerance = 1e-9;

    static string K(Guid id) => id.ToString("N");

    // walls: the walls standing on one level, ordered by id ("D", ordinal), each with valid wall geometry.
    public static IEnumerable<DocDimension> Chains(string view, IReadOnlyList<Element> walls)
    {
        static double Length(double[] g) => Math.Sqrt((g[2] - g[0]) * (g[2] - g[0]) + (g[3] - g[1]) * (g[3] - g[1]));
        var usable = walls.Where(wall => Length(wall.GeometryMetres) > Tolerance).ToArray();
        var assigned = new HashSet<Guid>();
        var result = new List<DocDimension>();
        foreach (var wall in usable)
        {
            if (assigned.Contains(wall.Id)) continue;
            var g = wall.GeometryMetres;
            var length = Length(g);
            double ox = g[0], oy = g[1], dx = (g[2] - g[0]) / length, dy = (g[3] - g[1]) / length, nx = -dy, ny = dx;
            double Across(double x, double y) => (x - ox) * nx + (y - oy) * ny;
            double Along(double x, double y) => (x - ox) * dx + (y - oy) * dy;
            bool OnLine(Element other) => Math.Abs(Across(other.GeometryMetres[0], other.GeometryMetres[1])) <= Tolerance
                && Math.Abs(Across(other.GeometryMetres[2], other.GeometryMetres[3])) <= Tolerance;
            var line = usable.Where(other => other.Id == wall.Id || (!assigned.Contains(other.Id) && OnLine(other))).ToArray();
            foreach (var member in line) assigned.Add(member.Id);
            var members = line.Select(member => member.Id).ToHashSet();

            int inside = 0, outside = 0;
            foreach (var other in usable)
            {
                if (members.Contains(other.Id)) continue;
                var o = other.GeometryMetres;
                foreach (var side in new[] { Across(o[0], o[1]), Across(o[2], o[3]) })
                {
                    if (side > Tolerance) inside++;
                    else if (side < -Tolerance) outside++;
                }
            }
            if ((inside > 0) == (outside > 0)) continue;
            double outX = inside > 0 ? -nx : nx, outY = inside > 0 ? -ny : ny;

            var along = line.SelectMany(member => new[] { Along(member.GeometryMetres[0], member.GeometryMetres[1]), Along(member.GeometryMetres[2], member.GeometryMetres[3]) }).ToArray();
            double t0 = along.Min(), t1 = along.Max();
            if (!(t1 - t0 > Tolerance)) continue;
            Element? EndWall(double x, double y) => usable.FirstOrDefault(other =>
            {
                if (members.Contains(other.Id)) return false;
                var o = other.GeometryMetres;
                var l = Length(o);
                if (Math.Abs((o[2] - o[0]) / l * dx + (o[3] - o[1]) / l * dy) > Tolerance) return false;
                return (Math.Abs(o[0] - x) <= Tolerance && Math.Abs(o[1] - y) <= Tolerance) || (Math.Abs(o[2] - x) <= Tolerance && Math.Abs(o[3] - y) <= Tolerance);
            });
            double e0x = ox + dx * t0, e0y = oy + dy * t0;
            var first = EndWall(e0x, e0y);
            var second = EndWall(ox + dx * t1, oy + dy * t1);
            if (first is null || second is null || first.Id == second.Id) continue;
            var a = first.GeometryMetres;
            var b = second.GeometryMetres;
            double la = Length(a), lb = Length(b), ux = (a[2] - a[0]) / la, uy = (a[3] - a[1]) / la;
            if (Math.Abs(ux * ((b[3] - b[1]) / lb) - uy * ((b[2] - b[0]) / lb)) > ParallelTolerance) continue;
            double sx = e0x + outX * ClearanceMetres, sy = e0y + outY * ClearanceMetres;
            var offset = Math.Round((sx - (a[0] + a[2]) / 2) * ux + (sy - (a[1] + a[3]) / 2) * uy, 9, MidpointRounding.ToEven);
            if (offset == 0) offset = 0.0;
            result.Add(new DocDimension("dim-wall-" + K(wall.Id), view, first.Id, second.Id, offset));
        }
        return result;
    }
}
