using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `scenario`: instance maps - the trigger areas (the map's .aid from client data), the areas the
/// server asks us to confirm, doors, the timer. Area names are returned as Lua strings (low-rate use).</summary>
[LuaModule("scenario")]
public sealed partial class ScenarioModule(AgentContext ctx) : ApiModule(ctx)
{
    private ScenarioState S => Ctx.World.Scenario;
    private readonly Intent[] _one = new Intent[1];

    /// <summary>Was any scenario packet seen on this map?</summary>
    [LuaFn] public bool Active() => S.Active;
    /// <summary>The oldest area the server asked us to confirm and we have not ("" none).</summary>
    [LuaFn(Alloc = Alloc.LuaString)] public string Asked() => S.Asked.Count > 0 ? S.Asked[0].Area : "";
    /// <summary>Was an area confirmed on this map?</summary>
    [LuaFn] public bool Confirmed(string area) => S.Confirmed.Contains(area ?? "");
    /// <summary>An area of the current map (client .aid): centre x, y and half extents (-1s when unknown).</summary>
    [LuaFn]
    public (double X, double Y, double HalfX, double HalfY) Area(string area)
    {
        foreach (var a in Ctx.Nav.Areas(Ctx.World.Map.MapName)) if (a.Name == area) return (a.X, a.Y, a.HalfX, a.HalfY);
        return (-1, -1, -1, -1);
    }
    /// <summary>Are we inside an area's box?</summary>
    [LuaFn]
    public bool Inside(string area)
    {
        var (x, y) = Ctx.World.Self.Pos;
        foreach (var a in Ctx.Nav.Areas(Ctx.World.Map.MapName)) if (a.Name == area) return a.Contains(x, y);
        return false;
    }
    /// <summary>Number of trigger areas the map has (client .aid).</summary>
    [LuaFn] public int AreaCount() => Ctx.Nav.Areas(Ctx.World.Map.MapName).Count;
    /// <summary>Confirm an area the server asked about (AREAENTRY_ACK with its name). Sent once; the server answers with
    /// the room's events (waves, doors), so the result is resolved when sent.</summary>
    [LuaFn]
    public int Confirm(string area)
    {
        area ??= "";
        Span<byte> b = stackalloc byte[32]; Ascii.Put(b, area);
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpAreaEntryAck, b, -1);
        var id = Ctx.Actions.Issue(ActionKind.Raw, CallingLayer, IntentEncoder.OpAreaEntryAck, 0, _one).Value;
        if (id >= 0) { S.Asked.RemoveAll(x => x.Area == area); S.Confirmed.Add(area); }
        return id;
    }
    /// <summary>Doors seen on this map (handles).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Doors() { var s = Ctx.Scratch(64); var n = 0; foreach (var k in S.Doors.Keys) if (n < s.Length) s[n++] = k; return ApiScope.Iter(s[..n]); }
    /// <summary>A door's state and position (-1 unknown door).</summary>
    [LuaFn] public (int State, double X, double Y) Door(int h) => S.Doors.TryGetValue(h, out var d) ? (d.State, d.X, d.Y) : (-1, -1, -1);
    /// <summary>The scenario object type of a handle (5 mob, 4 npc; -1 never changed).</summary>
    [LuaFn] public int ObjType(int h) => S.ObjTypes.TryGetValue(h, out var t) ? t : -1;
    /// <summary>Seconds left on the scenario timer (-1 none).</summary>
    [LuaFn] public long TimerLeft() => S.Timer.Seconds < 0 ? -1 : Math.Max(0, S.Timer.Seconds - (Ctx.Now - S.Timer.At) / 1000);
}
