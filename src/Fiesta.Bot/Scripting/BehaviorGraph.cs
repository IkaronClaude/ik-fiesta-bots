using System.Text.Json;

namespace Fiesta.Bot.Scripting;

/// <summary>A state node in a <see cref="BehaviorGraph"/>: a name plus the Lua source that
/// defines its lifecycle (<c>on_enter()</c>/<c>tick()</c>/<c>on_exit()</c>) and any event
/// hooks (<c>on_hp</c>, <c>on_hit</c>, …). Each state is its own visible node/script.</summary>
public sealed record GraphState(string Name, string Script);

/// <summary>A first-class transition edge <c>From → To</c>. Its <see cref="Check"/> Lua source
/// defines a <c>check()</c> that returns truthy to fire the transition. Evaluated every tick
/// while the graph is in <see cref="From"/>.</summary>
public sealed record GraphTransition(string Name, string From, string To, string Check);

/// <summary>A behaviour graph: states (nodes) + transitions (edges) + the <see cref="Initial"/>
/// state, plus an optional <see cref="Shared"/> helper script loaded into every node's and
/// transition's environment (so e.g. <c>mob_grind</c> can call a shared <c>survive()</c> that
/// <c>stay_alive</c> also uses — composition without duplication). Serializable for persistence.</summary>
public sealed record BehaviorGraph(
    string Name,
    string Initial,
    IReadOnlyList<GraphState> States,
    IReadOnlyList<GraphTransition> Transitions,
    string? Shared = null);

/// <summary>Disk persistence for behaviour graphs (one JSON file per graph). Survives host
/// restarts — which also fixes uploaded behaviour vanishing on a rebuild.</summary>
public sealed class GraphStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;

    public GraphStore(string dir)
    {
        _dir = dir;
        try { Directory.CreateDirectory(_dir); } catch { /* best effort */ }
    }

    private string PathFor(string name) => Path.Combine(_dir, name + ".json");

    public void Save(BehaviorGraph g) => File.WriteAllText(PathFor(g.Name), JsonSerializer.Serialize(g, Json));

    public BehaviorGraph? Load(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<BehaviorGraph>(File.ReadAllText(p), Json); }
        catch { return null; }
    }

    public bool Delete(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return false;
        try { File.Delete(p); return true; } catch { return false; }
    }

    public IReadOnlyList<string> List()
    {
        try { return Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null).Cast<string>().ToArray(); }
        catch { return []; }
    }

    // Per-bot current-state persistence (so a graph resumes its state across host restarts).
    private string StatePath(string graph, string botId) => Path.Combine(_dir, $"{graph}.{botId}.state");
    public void SaveState(string graph, string botId, string state)
    {
        try { File.WriteAllText(StatePath(graph, botId), state); } catch { /* best effort */ }
    }
    public string? LoadState(string graph, string botId)
    {
        var p = StatePath(graph, botId);
        try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; } catch { return null; }
    }
}
