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
}
