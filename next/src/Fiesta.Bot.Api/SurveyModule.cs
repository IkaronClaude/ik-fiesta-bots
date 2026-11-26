using System.Collections.Concurrent;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `survey`: sweep a map's walkable area and catalogue what is there (SurveyStore, SurveyLearner).
/// The route is the coverage cells of the map (SurveyRoute), their size from the measured sight radius; a cell counts as
/// seen once we have been within sight of its walkable point.</summary>
[LuaModule("survey")]
public sealed partial class SurveyModule(AgentContext ctx) : ApiModule(ctx)
{
    private static readonly ConcurrentDictionary<(string, int), List<(int Cx, int Cy, double X, double Y)>> _cells = new();
    private SurveyStore? Store => (Ctx.Knowledge as KnowledgeStore)?.Survey;
    private WorldState W => Ctx.World;

    /// <summary>The sight radius measured from where entities leave view (-1 nothing seen yet).</summary>
    [LuaFn] public double Sight() => Store?.SightRadius() ?? -1;

    // the cell size: a cell is wholly seen from its centre when its half-diagonal is the sight radius
    private double CellSize() { var r = Sight(); return r > 0 ? r * Math.Sqrt(2) : -1; }

    private List<(int Cx, int Cy, double X, double Y)>? Cells(string map)
    {
        var size = CellSize();
        if (size <= 0) return null;
        var key = (map, (int)Math.Round(size));
        if (_cells.TryGetValue(key, out var c)) return c;
        if (Ctx.Nav.Layer(map) is not { } layer || Ctx.Nav.Mesh(map) is not { } mesh) return null;   // loading off the tick
        var (x, y) = W.Self.Pos;
        return _cells[key] = SurveyRoute.Cells(layer, mesh, size, x, y);
    }

    /// <summary>The next point of the sweep on a map (marking first every cell within sight of where we stand as seen):
    /// the nearest unseen cell's walkable point; (-1, -1, seen, total) when the sweep is complete, (-2, -2, 0, 0) while
    /// the sight radius or the map's walk grid is not known yet.</summary>
    [LuaFn] public (double X, double Y, int Seen, int Total) Next(string map)
    {
        if (Store is not { } st || Cells(map) is not { } cells) return (-2, -2, 0, 0);
        var s = st.Of(map); var size = CellSize(); var r = Sight();
        s.CellSize = size;
        var (px, py) = W.Self.Pos;
        double bx = -1, by = -1, bd = double.MaxValue; var seen = 0;
        foreach (var c in cells)
        {
            var k = $"{c.Cx},{c.Cy}";
            if (!s.Seen.Contains(k) && Math.Sqrt((c.X - px) * (c.X - px) + (c.Y - py) * (c.Y - py)) <= r) s.Seen.Add(k);
            if (s.Seen.Contains(k)) { seen++; continue; }
            var d = Math.Abs(c.X - px) + Math.Abs(c.Y - py);
            if (d < bd) { bd = d; bx = c.X; by = c.Y; }
        }
        if (bx < 0 && s.CompletedUnixMs == 0) { s.CompletedUnixMs = SystemClock.UnixMs(Ctx.Now); st.Save(map); }
        return (bx, by, seen, cells.Count);
    }

    /// <summary>A cell we could not reach is set aside as seen (a walk that fails is not tried forever).</summary>
    [LuaFn] public void Skip(string map, double x, double y)
    {
        if (Store is not { } st || Cells(map) is not { } cells) return;
        foreach (var c in cells) if (Math.Abs(c.X - x) < 1 && Math.Abs(c.Y - y) < 1) st.Of(map).Seen.Add($"{c.Cx},{c.Cy}");
    }

    /// <summary>Has a sweep of this map completed?</summary>
    [LuaFn] public bool Done(string map) => Store?.Of(map).CompletedUnixMs > 0;

    /// <summary>Persist what was seen on a map.</summary>
    [LuaFn] public void Save(string map) => Store?.Save(map);

    /// <summary>A summary of a map's catalogue for a log line.</summary>
    [LuaFn(Cost = Cost.On, Alloc = Alloc.LuaString)] public string Summary(string map)
    {
        if (Store is not { } st) return "no survey store";
        var s = st.Of(map);
        var sightings = s.Mobs.Values.Sum(l => l.Count);
        var resp = s.Respawns.Where(kv => kv.Value.Count > 0).Select(kv => $"{Ctx.Data.Mob(kv.Key)?.Name ?? kv.Key.ToString()} {kv.Value.Average() / 1000:0} s").Take(5);
        return $"{map}: {s.Mobs.Count} mob types ({sightings} sightings), {s.Npcs.Count} npcs, {s.Gates.Count} gates, {s.Drops.Count} drop kinds, cells seen {s.Seen.Count}"
            + (s.Respawns.Count > 0 ? "; respawns " + string.Join(", ", resp) : "")
            + "; mobs " + string.Join(", ", s.Mobs.OrderByDescending(kv => kv.Value.Count).Take(6).Select(kv => $"{Ctx.Data.Mob(kv.Key)?.Name ?? kv.Key.ToString()} lv{kv.Value[^1].Level} x{kv.Value.Count}"));
    }
}
