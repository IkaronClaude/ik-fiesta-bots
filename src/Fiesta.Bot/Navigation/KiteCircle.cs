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

    /// <summary>Required loop DIAMETER as a multiple of the chaser's attack range.
    ///
    /// <para>Started at 4x on the operator's first estimate, then MEASURED against the terrain we
    /// actually fight on (tools/kite_fit_probe.py) and lowered to 2.5x on their call. The measurement is
    /// why: the JCQ clone room (Job1_Dn01) holds a largest walkable circle of r=524u — a 1049u diameter —
    /// while 4x against the clone's ~400u range demands 1600u. No circle could ever fit, and 81 of 82
    /// fits failed there. At 2.5x the floor is 1.25x range, so that same room supports an enemy range up
    /// to ~419u and the clone fits with a little margin.</para>
    ///
    /// <para>Lower is a real trade: the tighter the loop, the more of its time the chaser spends in range
    /// rather than travelling. 2.5x is the widest the venue allows, not the widest that would be good.</para></summary>
    public const double DiameterPerRange = 2.5;

    /// <param name="maxDiameter">Upper bound on the loop size in world units.</param>
    /// <param name="enemyRange">The chaser's attack range. The loop's DIAMETER must be at least
    /// <see cref="DiameterPerRange"/> x this, or the manoeuvre is pointless (operator 2026-08-13): on a
    /// tight loop "enemy just needs to take small adjustment steps to attack, so its 'attacking time'
    /// will be like 90%". Wide enough, it has to spend most of its time travelling instead — the goal is
    /// "it can only hit us like 20% of the time or so".</param>
    /// <param name="leeway">How far OUTSIDE the rim the bot may start. It only has to "almost contain"
    /// us (operator 2026-08-13) — a loop we are a little outside of is still cheap to ride onto, and
    /// insisting on strict containment throws away good circles. The ENEMY deliberately does NOT have to
    /// be inside: an earlier version required that and the operator corrected it.</param>
    /// <returns>(CentreX, CentreY, Radius) of the largest fitting circle, or null when the ground will
    /// not hold one that satisfies the minimum — the caller then falls back to the straight-line kite
    /// (and should say so, because that kite ends at the first wall).</returns>
    public static (double Cx, double Cy, double R)? Fit(
        BlockGrid grid, double px, double py,
        double maxDiameter = 5000, double enemyRange = 400, double leeway = 100)
    {
        // The rule is expressed as a DIAMETER multiple, so the radius floor is half of it.
        var minRadius = Math.Max(150, enemyRange * DiameterPerRange / 2);
        var maxR = maxDiameter / 2.0;
        if (minRadius > maxR) return null;      // cannot be both big enough to work and within the cap
        // Coarse-to-fine on the radius: the FIRST radius that fits anywhere is the biggest, so stop there.
        for (var r = maxR; r >= minRadius; r *= 0.8)
        {
            foreach (var (cx, cy) in Centres(px, py, r, leeway))
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

    private static IEnumerable<(double X, double Y)> Centres(double px, double py, double r, double leeway)
    {
        yield return (px, py);                       // we are dead centre — always start inside
        // Slide the centre around us, out to where we sit `leeway` OUTSIDE the rim. Strict containment
        // rejects circles that are perfectly good to ride onto from just outside, so the search goes a
        // little past r; the blended hand-off pulls us back onto the rim from there anyway.
        foreach (var frac in new[] { 0.34, 0.66, 1.0 })
            for (var i = 0; i < 8; i++)
            {
                var a = i * (Math.PI / 4);
                var d = Math.Min(r * frac, r + leeway);
                yield return (px + Math.Cos(a) * d, py + Math.Sin(a) * d);
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
