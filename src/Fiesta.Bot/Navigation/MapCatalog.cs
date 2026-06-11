using System.Collections.Concurrent;

namespace Fiesta.Bot.Navigation;

/// <summary>
/// Resolves a server map id (the <c>MapInfo.ID</c> the zone puts in a
/// <see cref="MapHandoff"/>) to its short map name — the name a block grid file
/// (<c>&lt;name&gt;.shbd</c>) and a gate's <c>LinkMap</c> use (e.g. id 9 → "Eld",
/// id 150 → "RouN").
///
/// <para>Learning-first, by design: the bot doesn't need a prebuilt table. When it
/// uses a gate it already knows the destination <i>name</i> (the gate's
/// <c>LinkMap</c>), and the resulting handoff carries the <i>id</i> — so
/// <see cref="Learn"/> pairs the two. Over a session the catalog fills itself in,
/// which is exactly the "learn the server by playing it" auto-discovery path for
/// when server files aren't available.</para>
///
/// <para>An optional bootstrap (<see cref="LoadSeed"/>, from a BYO <c>id,name</c>
/// CSV pointed at by <c>MAPINFO_PATH</c>) can pre-fill it from the server MapInfo
/// table so the very first cross-server hop can pick a grid without having walked
/// the gate first. Thread-safe.</para>
/// </summary>
public sealed class MapCatalog
{
    private readonly ConcurrentDictionary<ushort, string> _idToName = new();
    private readonly ConcurrentDictionary<string, ushort> _nameToId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record an id↔name pairing (e.g. learned from a gate's LinkMap + the
    /// handoff id, or seeded from MapInfo). Later learnings win.</summary>
    public void Learn(ushort id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _idToName[id] = name;
        _nameToId[name] = id;
    }

    public string? NameFor(ushort id) => _idToName.TryGetValue(id, out var n) ? n : null;
    public ushort? IdFor(string name) => _nameToId.TryGetValue(name, out var id) ? id : null;

    public int Count => _idToName.Count;

    /// <summary>Pre-fill from a CSV of <c>id,name</c> lines (header tolerated). Lines
    /// that don't parse are skipped. Returns the number of rows learned.</summary>
    public int LoadSeed(IEnumerable<string> csvLines)
    {
        var n = 0;
        foreach (var raw in csvLines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            if (!ushort.TryParse(line[..comma].Trim(), out var id)) continue; // skips a header row
            var name = line[(comma + 1)..].Trim();
            if (name.Length == 0) continue;
            Learn(id, name);
            n++;
        }
        return n;
    }

    /// <summary>Load a seed from <c>MAPINFO_PATH</c> if it points at a readable file.
    /// Returns rows learned (0 if unset/missing). BYO — never shipped in the repo.</summary>
    public int LoadSeedFromEnv()
    {
        var path = Environment.GetEnvironmentVariable("MAPINFO_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        try { return LoadSeed(File.ReadLines(path)); }
        catch { return 0; }
    }
}
