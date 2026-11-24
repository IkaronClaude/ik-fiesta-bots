namespace Fiesta.Bot.LuaBench;

/// <summary>The C# side of the benchmark: a tiny stand-in for one bot's world (self + nearby mobs), so every
/// engine's callbacks do the same small amount of real work.</summary>
public sealed class World
{
    public const int MobCount = 50;
    public readonly double[] X = new double[MobCount];
    public readonly double[] Y = new double[MobCount];
    public readonly double[] Hp = new double[MobCount];
    public double SelfX = 5000, SelfY = 5000, SelfHp = 812;
    public long LogBytes;
    public int LogCalls;
    public readonly string MapName = "RouVal02";

    public World(int seed)
    {
        var r = new Random(seed);
        for (int i = 0; i < MobCount; i++)
        {
            X[i] = SelfX + r.Next(-1200, 1200);
            Y[i] = SelfY + r.Next(-1200, 1200);
            Hp[i] = r.Next(100, 2000);
        }
    }

    public double Dist(long h)
    {
        if ((ulong)h >= MobCount) return 1e9;
        double dx = X[h] - SelfX, dy = Y[h] - SelfY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double MobHp(long h) => (ulong)h < MobCount ? Hp[h] : 0;

    /// <summary>Generic, data-driven usability check (stands in for the real canUse: cooldown, cost, range...)</summary>
    public bool CanUse(long skill, long target) => ((skill * 7 + target) & 3) != 0 && Dist(target) < 1000;

    /// <summary>Iterator step: next mob handle after <paramref name="prev"/>, or -1 when done</summary>
    public long Next(long prev) => prev + 1 < MobCount ? prev + 1 : -1;
}
