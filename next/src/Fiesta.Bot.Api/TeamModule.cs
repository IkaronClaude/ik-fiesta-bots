using Fiesta.Bot.Agent;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `team`: the host-wide board where our bots ask each other for help (money for stones, ...).
/// An ask is posted with the character's map and position; a helper takes it on with a claim, comes over and trades.
/// Low-rate calls (a few per minute): names and maps are returned as Lua strings.</summary>
[LuaModule("team")]
public sealed partial class TeamModule(AgentContext ctx) : ApiModule(ctx)
{
    private string Me => Ctx.World.Self.Name;

    /// <summary>Post or refresh our ask of a kind for an amount, waiting where we stand.</summary>
    [LuaFn]
    public bool Ask(string kind, long amount)
    {
        if (Me.Length == 0) return false;
        var (x, y) = Ctx.World.Self.Pos;
        Ctx.Team.Ask(Me, kind ?? "", amount, Ctx.World.Map.MapName, x, y, Ctx.Now);
        return true;
    }

    /// <summary>Withdraw our ask.</summary>
    [LuaFn] public void Withdraw() { if (Me.Length > 0) Ctx.Team.Withdraw(Me); }

    /// <summary>Our live ask: kind ("" none), amount, claimer ("" nobody yet).</summary>
    [LuaFn(Alloc = Alloc.LuaString)]
    public (string Kind, long Amount, string Claimer) Mine() => Ctx.Team.Get(Me, Ctx.Now) is { } a ? (a.Kind, a.Amount, a.Claimer) : ("", 0, "");

    /// <summary>The ask of a kind we could help with (amount at most maxAmount; the one we claimed first): name ("" none),
    /// amount, map, x, y.</summary>
    [LuaFn(Alloc = Alloc.LuaString)]
    public (string Name, long Amount, string Map, double X, double Y) Next(string kind, long maxAmount) =>
        Ctx.Team.Next(Me, kind ?? "", maxAmount, Ctx.Now) is { } a ? (a.Name, a.Amount, a.Map, a.X, a.Y) : ("", 0, "", -1, -1);

    /// <summary>Take on a character's ask (true when it is ours now).</summary>
    [LuaFn] public bool Claim(string name) => Me.Length > 0 && Ctx.Team.Claim(name ?? "", Me);

    /// <summary>Give a claimed ask back.</summary>
    [LuaFn] public void Release(string name) { if (Me.Length > 0) Ctx.Team.Release(name ?? "", Me); }

    /// <summary>The handle of a player in view by character name, -1 none.</summary>
    [LuaFn]
    public int Player(string name)
    {
        var w = Ctx.World; var s = Ctx.Scratch(1024); var (x, y) = w.Self.Pos;
        var n = w.Grid.Query(x, y, 4000, EntityKind.Player, w, Ctx.Now, s);
        for (var i = 0; i < n; i++) if (w.Entities.NameOf(s[i]) == name) return w.Entities.HandleOf(s[i]);
        return -1;
    }
}
