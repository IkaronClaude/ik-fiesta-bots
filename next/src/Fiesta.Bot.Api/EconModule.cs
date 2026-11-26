using Fiesta.Bot.Agent;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `econ`: the bot's economy over the last hour (exp, money, stone spend, deaths), as rates per hour
/// over the last N minutes. What the brain judges a death spiral or a draining purse by.</summary>
[LuaModule("econ")]
public sealed partial class EconModule(AgentContext ctx) : ApiModule(ctx)
{
    /// <summary>Per-hour rates over the last <paramref name="minutes"/> minutes (1..60): exp, money (earned minus spent),
    /// deaths, money spent on soul stones.</summary>
    [LuaFn]
    public (double ExpH, double MoneyH, double DeathsH, double StonesH) Rates(int minutes) => Ctx.World.Economy.Rates(minutes, Ctx.Now);

    /// <summary>What we measured against a mob type, learned as we play: kills, mean kill time (ms), stones used per
    /// kill, exp per kill, deaths by it (kills 0 = never fought: the caller falls back to its own estimate).</summary>
    [LuaFn]
    public (int Kills, double KillMs, double StonesPerKill, double ExpPerKill, int Deaths) Mob(int mobId)
    {
        var c = Ctx.World.Economy.Mob(mobId);
        return c.Kills == 0 ? (0, -1, -1, -1, c.Deaths) : (c.Kills, (double)c.FightMs / c.Kills, (double)c.Stones / c.Kills, (double)c.Exp / c.Kills, c.Deaths);
    }

    /// <summary>The share of the last <paramref name="minutes"/> minutes spent walking on foot (0..1) and the minutes
    /// measured: what a mount would shorten.</summary>
    [LuaFn] public (double Share, double Minutes) WalkShare(int minutes) => Ctx.World.Economy.WalkShare(minutes, Ctx.Now);
    /// <summary>Money spent per hour over the last <paramref name="minutes"/> minutes: all of it, and the soul stones' part.</summary>
    [LuaFn] public (double SpendH, double StonesH) Spend(int minutes) => Ctx.World.Economy.Spend(minutes, Ctx.Now);

    /// <summary>Exp that came with no kill (quest rewards) since the session began.</summary>
    [LuaFn] public long QuestExp() => Ctx.World.Economy.QuestExp;

    /// <summary>Exp the game took for deaths (BAT_EXPLOST): average per loss and the number of losses seen (-1, 0 none).</summary>
    /// <summary>The fraction of the level's span a death takes at a level, learned from deaths: 0 at or below the highest
    /// level a death cost nothing at, the measured fraction where losses were seen, -1 not known yet (use a prior).</summary>
    [LuaFn] public double DeathLossFraction(int level) => Ctx.World.Economy.LossFraction(level);
    /// <summary>Record an observed no-loss death level (operator input from earlier logs); the new no-loss level.</summary>
    [LuaFn] public int NoteNoLossLevel(int level) { Ctx.World.Economy.NoteNoLoss(level); return Ctx.World.Economy.LossLevels.NoLossMax; }
    /// <summary>The highest level a death was seen to cost no exp at (-1 none).</summary>
    [LuaFn] public int NoLossLevel() => Ctx.World.Economy.LossLevels.NoLossMax;
    [LuaFn] public (double Avg, int Count) ExpLost() { var (s, n) = Ctx.World.Economy.ExpLost; return n > 0 ? ((double)s / n, n) : (-1, 0); }
}
