using System.Collections.Concurrent;
using System.Text.Json;

namespace Fiesta.Bot.Services;

/// <summary>What bots have seen on each map (a host-wide survey, persisted per map): mobs (id, level, where, when), NPCs,
/// gates and drops, the coverage cells a sweep has seen, and the sight radius measured from where entities leave view.
/// Observed spawn areas come from it and outrank the client's MobCoordinate where they disagree.</summary>
public sealed class SurveyStore
{
    /// <summary>One sighting.</summary>
    public sealed record Sight(int Id, int Level, double X, double Y, long UnixMs);

    /// <summary>One map's catalogue.</summary>
    public sealed class MapSurvey
    {
        /// <summary>Mobs by id: their sightings (capped per id).</summary>
        public Dictionary<int, List<Sight>> Mobs { get; set; } = new();
        /// <summary>NPCs by id: where they stand.</summary>
        public Dictionary<int, Sight> Npcs { get; set; } = new();
        /// <summary>Gates by "x,y": the destination map named.</summary>
        public Dictionary<string, string> Gates { get; set; } = new();
        /// <summary>Drops by item id: how many seen.</summary>
        public Dictionary<int, int> Drops { get; set; } = new();
        /// <summary>Drops by the mob that dropped them: mob id -> item id -> how many (the drop frame names its dropper).</summary>
        public Dictionary<int, Dictionary<int, int>> DropsBy { get; set; } = new();
        /// <summary>Deaths seen per mob id (the denominator of a drop rate).</summary>
        public Dictionary<int, int> Deaths { get; set; } = new();
        /// <summary>Spawns seen by mob id: where and when one appeared through its spawn in our view (capped per id).</summary>
        public Dictionary<int, List<Sight>> Spawns { get; set; } = new();
        /// <summary>Respawn gaps by mob id (ms): from a death of the type to the next spawn of the type near it.</summary>
        public Dictionary<int, List<long>> Respawns { get; set; } = new();
        /// <summary>Coverage cells seen ("cx,cy" in the plan's cell grid) and the cell size they were planned with.</summary>
        public HashSet<string> Seen { get; set; } = new();
        /// <summary>The cell size the coverage was planned with (world units).</summary>
        public double CellSize { get; set; }
        /// <summary>When the sweep completed (unix ms), 0 never.</summary>
        public long CompletedUnixMs { get; set; }
    }

    /// <summary>Sightings kept per mob id per map (bot behaviour: enough for a spawn area and its spread).</summary>
    public const int SightsPerMob = 200;

    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, MapSurvey> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly double[] _leave = new double[512]; private int _leaveN;
    private double _appearMax;
    // deaths waiting for their respawn: (map, mob id, x, y) -> unix ms
    private readonly ConcurrentDictionary<(string, int, int, int), long> _deathAt = new();

    /// <summary>Create over a folder (null: in memory only).</summary>
    public SurveyStore(string? dir) { _dir = dir; if (dir is not null) Directory.CreateDirectory(dir); }

    /// <summary>A map's catalogue (loaded on first use).</summary>
    public MapSurvey Of(string map) => _maps.GetOrAdd(map, m =>
    {
        try
        {
            var f = _dir is null ? null : Path.Combine(_dir, m + ".json");
            if (f is not null && File.Exists(f)) return JsonSerializer.Deserialize<MapSurvey>(File.ReadAllText(f)) ?? new();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
    });

    /// <summary>Write a map's catalogue.</summary>
    public void Save(string map)
    {
        if (_dir is null) return;
        lock (_lock) File.WriteAllText(Path.Combine(_dir, map + ".json"), JsonSerializer.Serialize(Of(map)));
    }

    // ---- sight radius

    /// <summary>A live entity left view at this distance (a sample of the sight radius).</summary>
    public void Left(double dist) { lock (_lock) _leave[_leaveN++ % _leave.Length] = dist; }
    /// <summary>An entity came into view at this distance.</summary>
    public void Appeared(double dist) { lock (_lock) if (dist > _appearMax) _appearMax = dist; }

    /// <summary>The samples the sight radius needs before its percentile replaces the farthest appearance (bot behaviour:
    /// below this a percentile is noise).</summary>
    public const int MinSightSamples = 20;

    /// <summary>The sight radius: the 90th percentile of the distances live entities left view at; before enough of those,
    /// the farthest an entity has appeared at; -1 nothing seen yet.</summary>
    public double SightRadius()
    {
        lock (_lock)
        {
            var n = Math.Min(_leaveN, _leave.Length);
            if (n >= MinSightSamples) { var a = _leave[..n]; Array.Sort(a); return a[(int)(0.9 * (n - 1))]; }
            return _appearMax > 0 ? _appearMax : -1;
        }
    }

    // ---- the catalogue

    /// <summary>A mob seen; <paramref name="spawned"/>: it appeared through its spawn (not coming into view).</summary>
    public void SawMob(string map, int mobId, int level, double x, double y, long unixMs, bool spawned = false)
    {
        var s = Of(map);
        lock (_lock)
        {
            if (!s.Mobs.TryGetValue(mobId, out var l)) s.Mobs[mobId] = l = new();
            l.Add(new Sight(mobId, level, x, y, unixMs));
            if (l.Count > SightsPerMob) l.RemoveAt(0);
            if (!spawned) return;
            if (!s.Spawns.TryGetValue(mobId, out var sp)) s.Spawns[mobId] = sp = new();
            sp.Add(new Sight(mobId, level, x, y, unixMs));
            if (sp.Count > SightsPerMob) sp.RemoveAt(0);
            // a respawn gap: from the latest death of the type within a sight radius of the spawn (spawns scatter over
            // their area; a death that far away is another spawn's)
            var r0 = SightRadius(); var near = r0 > 0 ? r0 : DefaultRespawnNear;
            (int, int, int, int) bestKey = default; long died = -1; var found = false;
            foreach (var kv in _deathAt)
            {
                var (m, id, dx, dy) = kv.Key;
                if (id != mobId || !string.Equals(m, map, StringComparison.OrdinalIgnoreCase)) continue;
                if (Math.Sqrt((dx - x) * (dx - x) + (dy - y) * (dy - y)) > near || kv.Value >= unixMs || kv.Value <= died) continue;
                bestKey = (0, id, dx, dy); died = kv.Value; found = true;
            }
            if (!found) return;
            _deathAt.TryRemove((map, mobId, bestKey.Item3, bestKey.Item4), out _);
            if (!s.Respawns.TryGetValue(mobId, out var r)) s.Respawns[mobId] = r = new();
            r.Add(unixMs - died); if (r.Count > 50) r.RemoveAt(0);
        }
    }

    /// <summary>How near a death a spawn must be to close its respawn gap before the sight radius is measured (bot
    /// behaviour: about a screen).</summary>
    public const double DefaultRespawnNear = 1000;

    /// <summary>A mob died here (for respawn gaps and drop rates).</summary>
    public void MobDied(string map, int mobId, double x, double y, long unixMs)
    {
        _deathAt[(map, mobId, (int)x, (int)y)] = unixMs;
        var s = Of(map);
        lock (_lock) s.Deaths[mobId] = s.Deaths.GetValueOrDefault(mobId) + 1;
    }

    private int Cell(double v) { var r = SightRadius(); return (int)Math.Floor(v / (r > 0 ? r : 1000)); }

    /// <summary>An NPC seen.</summary>
    public void SawNpc(string map, int npcId, double x, double y, long unixMs) { var s = Of(map); lock (_lock) s.Npcs[npcId] = new Sight(npcId, 0, x, y, unixMs); }
    /// <summary>A gate seen.</summary>
    public void SawGate(string map, double x, double y, string dest) { var s = Of(map); lock (_lock) s.Gates[$"{x:0},{y:0}"] = dest; }
    /// <summary>A drop seen; <paramref name="byMob"/>: the mob id that dropped it (-1 unknown).</summary>
    public void SawDrop(string map, int itemId, int byMob = -1)
    {
        var s = Of(map);
        lock (_lock)
        {
            s.Drops[itemId] = s.Drops.GetValueOrDefault(itemId) + 1;
            if (byMob < 0) return;
            if (!s.DropsBy.TryGetValue(byMob, out var d)) s.DropsBy[byMob] = d = new();
            d[itemId] = d.GetValueOrDefault(itemId) + 1;
        }
    }

    /// <summary>The mobs seen dropping an item on any surveyed map: (map, mob id, drops, deaths seen).</summary>
    public IEnumerable<(string Map, int MobId, int Drops, int Deaths)> DroppersOf(int itemId)
    {
        foreach (var (map, s) in _maps)
        {
            List<(string, int, int, int)> found;
            lock (_lock) found = s.DropsBy.Where(kv => kv.Value.ContainsKey(itemId)).Select(kv => (map, kv.Key, kv.Value[itemId], s.Deaths.GetValueOrDefault(kv.Key))).ToList();
            foreach (var f in found) yield return f;
        }
    }

    /// <summary>A mob's observed spawn area on a map: the densest cluster of its sightings (cells of the sight radius),
    /// its centroid and the spread around it; null when never seen.</summary>
    public (double X, double Y, double R, int N)? ObservedArea(string map, int mobId)
    {
        var s = Of(map);
        List<Sight> l;
        // its spawn points when any were seen (where it appears, not where it wandered or chased us to), else sightings
        lock (_lock)
        {
            if (s.Spawns.TryGetValue(mobId, out var sp) && sp.Count > 0) l = sp.ToList();
            else if (s.Mobs.TryGetValue(mobId, out var x) && x.Count > 0) l = x.ToList();
            else return null;
        }
        var best = l.GroupBy(p => (Cell(p.X), Cell(p.Y))).OrderByDescending(g => g.Count()).First().ToList();
        double cx = best.Average(p => p.X), cy = best.Average(p => p.Y);
        var r = best.Max(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
        return (cx, cy, Math.Max(r, 1), best.Count);
    }

    /// <summary>Write every map's catalogue (what bots saw passing through counts as much as a sweep).</summary>
    public void SaveAll() { foreach (var m in _maps.Keys) { try { Save(m); } catch (IOException) { } } }

    /// <summary>Maps with a catalogue.</summary>
    public IEnumerable<string> Maps => _maps.Keys;
}

/// <summary>A coverage sweep of a map's walkable area: cells of side sight x sqrt 2 (a cell is wholly seen from its
/// centre), only cells with a walkable tile in the region we stand in, visited nearest-first; a cell counts as seen
/// once we have been within the sight radius of its walkable point, so what a walk passes on its way is not walked again.</summary>
public static class SurveyRoute
{
    /// <summary>The walkable point of every cell of a map's grid that lies in the same connected region as (x, y).</summary>
    public static List<(int Cx, int Cy, double X, double Y)> Cells(BlockLayer layer, NavMesh mesh, double cell, double x, double y)
    {
        var list = new List<(int, int, double, double)>();
        var (sx, sy) = BlockLayer.ToTile(x, y);
        var region = mesh.RegionAt(sx, sy);
        var comp = region >= 0 ? mesh.Component[region] : -1;
        var w = layer.Width * BlockLayer.WorldPerTile; var h = layer.Height * BlockLayer.WorldPerTile;
        for (var cy = 0; cy * cell < h; cy++)
            for (var cx = 0; cx * cell < w; cx++)
            {
                // the walkable tile nearest the cell's centre, in our region
                double bx = -1, by = -1, bd = double.MaxValue;
                var step = BlockLayer.WorldPerTile * 4;
                for (var py = cy * cell; py < (cy + 1) * cell && py < h; py += step)
                    for (var px = cx * cell; px < (cx + 1) * cell && px < w; px += step)
                    {
                        var (tx, ty) = BlockLayer.ToTile(px, py);
                        if (layer.Blocked(tx, ty)) continue;
                        var rg = mesh.RegionAt(tx, ty);
                        if (rg < 0 || (comp >= 0 && mesh.Component[rg] != comp)) continue;
                        var d = Math.Abs(px - (cx + 0.5) * cell) + Math.Abs(py - (cy + 0.5) * cell);
                        if (d < bd) { bd = d; bx = px; by = py; }
                    }
                if (bx >= 0) list.Add((cx, cy, bx, by));
            }
        return list;
    }
}
