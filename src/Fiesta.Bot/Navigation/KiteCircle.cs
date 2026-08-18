using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

/// <summary>Fits the largest WALKABLE circle we can kite around, from the map's .shbd</summary>
public static class KiteCircle
{
    /// <summary>How many points around the rim are tested for walkability (and returned as waypoints)</summary>
    public const int Samples = 32;

    public const double DiameterPerRange = 2.5;

    public static (double Cx, double Cy, double R)? Fit(
        BlockGrid grid, double px, double py,
        double maxDiameter = 5000, double enemyRange = 400, double leeway = 100)
    {
        // The rule is expressed as a DIAMETER multiple, so the radius floor is half of it
        var minRadius = Math.Max(150, enemyRange * DiameterPerRange / 2);
        var maxR = maxDiameter / 2.0;
        if (minRadius > maxR) return null;      // cannot be both big enough to work and within the cap
        // Coarse-to-fine on the radius: the FIRST radius that fits anywhere is the biggest, so stop there
        for (var r = maxR; r >= minRadius; r *= 0.8)
        {
            foreach (var (cx, cy) in Centres(px, py, r, leeway))
            {
                if (RimWalkable(grid, cx, cy, r)) return (cx, cy, r);
            }
        }
        return null;
    }

    /// <summary>The rim as ordered world waypoints, starting from the bearing nearest</summary>
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
        // Slide the centre around us, out to where we sit `leeway` OUTSIDE the rim
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
        // Every sampled rim point must be walkable, AND so must the midpoint between consecutive samples, because a rim…
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
