using System.Collections.Concurrent;

namespace Fiesta.Bot.Navigation;

/// <summary>Resolves a server map id (the MapInfo.ID the zone puts in a ) to its short map name — the name a block grid fi…</summary>
public sealed class MapCatalog
{
    private readonly ConcurrentDictionary<ushort, string> _idToName = new();
    private readonly ConcurrentDictionary<string, ushort> _nameToId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record an id↔name pairing</summary>
    public void Learn(ushort id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _idToName[id] = name;
        _nameToId[name] = id;
    }

    public string? NameFor(ushort id) => _idToName.TryGetValue(id, out var n) ? n : null;
    public ushort? IdFor(string name) => _nameToId.TryGetValue(name, out var id) ? id : null;

    public int Count => _idToName.Count;

    /// <summary>Pre-fill from a CSV of id,name lines (header tolerated)</summary>
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

    /// <summary>Load a seed from MAPINFO_PATH if it points at a readable file</summary>
    public int LoadSeedFromEnv()
    {
        var path = Environment.GetEnvironmentVariable("MAPINFO_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        try { return LoadSeed(File.ReadLines(path)); }
        catch { return 0; }
    }
}
