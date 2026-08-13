using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

/// <summary>
/// Fits the largest WALKABLE circle we can kite around, from the map's <c>.shbd</c>.
///
/// <para><b>Why a circle at all</b> (operator 2026-08-13). A straight-line kite works right up until the
/// terrain ends: <i>"once you run into a wall (or down a corridor) you will likely have to hard turn,
/// during this turn you will be in enemy range for like 10 secs which is often deadly."</i> A closed loop
/// has no such moment — you can ride it forever. The chaser does NOT run the loop; it cuts across,
/// catching up and attacking in straight segments, which is the octagon the operator described. Ours is
/// the smooth path; the polygon is what the pursuit looks like from the outside.</para>
///
/// <para><b>Bigger is better</b> — a wide loop means the chaser's chords are long and it spends its time
/// travelling rather than attacking. Capped at <c>maxDiameter</c> (operator: "max diameter probs 5000u
/// or so") because beyond that we are just touring the map.</para>
///
/// <para><b>Only the PERIMETER has to be walkable</b>, since that is the only part we ever stand on. That
/// keeps the fit cheap and lets a circle enclose obstacles it never touches — a pillar in the middle of
/// the ring is harmless, and in fact ideal, because it blocks the chaser's shortcut.</para>
///
/// <para>The centre is searched near the bot so the loop is one we can reach: the caller must start
/// INSIDE the circle (or just outside it) for the tangential hand-off to be cheap.</para>
/// </summary>
public static class KiteCircle
{
    /// <summary>How many points around the rim are tested for walkability (and returned as waypoints).</summary>
    public const int Samples = 32;

    /// <param name="maxDiameter">Upper bound on the loop size in world units.</param>
    /// <param name="minRadius">Below this a "circle" is too tight to outrun anything on.</param>
    /// <returns>(CentreX, CentreY, Radius) of the largest fitting circle, or null when the ground will
    /// not hold one — the caller then falls back to the straight-line kite.</returns>
    public static (double Cx, double Cy, double R)? Fit(
        BlockGrid grid, double px, double py, double maxDiameter = 5000, double minRadius = 300)
    {
        var maxR = maxDiameter / 2.0;
        // Coarse-to-fine on the radius: the FIRST radius that fits anywhere is the biggest, so stop there.
        for (var r = maxR; r >= minRadius; r *= 0.8)
        {
            // Centre candidates: the bot itself first (guarantees we start inside), then offsets, which
            // let the loop slide off a wall we happen to be standing against while still enclosing us.
            foreach (var (cx, cy) in Centres(px, py, r))
            {
                if (RimWalkable(grid, cx, cy, r)) return (cx, cy, r);
            }
        }
        return null;
    }

    /// <summary>The rim as ordered world waypoints, starting from the bearing nearest <paramref name="fromAngle"/>.</summary>
    public static List<(uint X, uint Y)> Rim(double cx, double cy, double r, double fromAngle = 0)
    {
        var pts = new List<(uint, uint)>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var a = fromAngle + i * (2 * Math.PI / Samples);
            pts.Add(((uint)Math.Max(0, cx + Math.Cos(a) * r), (uint)Math.Max(0, cy + Math.Sin(a) * r)));
        }
        return pts;
    }

    private static IEnumerable<(double X, double Y)> Centres(double px, double py, double r)
    {
        yield return (px, py);                       // we are dead centre — always start inside
        // Slide the centre around us at a third and two-thirds of the radius. Keeping |centre - bot| < r
        // means the bot still starts INSIDE the ring, which is what makes the hand-off onto it cheap.
        foreach (var frac in new[] { 0.34, 0.66 })
            for (var i = 0; i < 8; i++)
            {
                var a = i * (Math.PI / 4);
                yield return (px + Math.Cos(a) * r * frac, py + Math.Sin(a) * r * frac);
            }
    }

    private static bool RimWalkable(BlockGrid grid, double cx, double cy, double r)
    {
        // Every sampled rim point must be walkable, AND so must the midpoint between consecutive samples,
        // because a rim can otherwise skip across a thin wall between two good points.
        double prevX = 0, prevY = 0;
        for (var i = 0; i <= Samples; i++)
        {
            var a = i * (2 * Math.PI / Samples);
            var x = cx + Math.Cos(a) * r;
            var y = cy + Math.Sin(a) * r;
            if (x < 0 || y < 0) return false;
            if (!grid.IsWalkableWorld((uint)x, (uint)y)) return false;
            if (i > 0 && !grid.IsWalkableWorld((uint)((x + prevX) / 2), (uint)((y + prevY) / 2))) return false;
            prevX = x; prevY = y;
        }
        return true;
    }
}
