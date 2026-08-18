using System.Collections.Concurrent;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>One stored behaviour script in the library</summary>
public sealed record StoredScript(string Name, string Source, DateTime UpdatedUtc);

/// <summary>In-memory library of uploaded Lua behaviour scripts, keyed by name</summary>
public sealed class ScriptStore
{
    private readonly ConcurrentDictionary<string, StoredScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compile-check and store/replace a script</summary>
    public (bool Ok, string? Error) Upsert(string name, string source)
    {
        if (string.IsNullOrWhiteSpace(name)) return (false, "name is required");
        if (string.IsNullOrEmpty(source)) return (false, "source is required");
        if (Compile(source) is { } err) return (false, err);
        _scripts[name] = new StoredScript(name, source, DateTime.UtcNow);
        return (true, null);
    }

    public StoredScript? Get(string name) => _scripts.TryGetValue(name, out var s) ? s : null;
    public IReadOnlyList<StoredScript> List() => _scripts.Values.OrderBy(s => s.Name).ToArray();
    public bool Delete(string name) => _scripts.TryRemove(name, out _);

    /// <summary>Parse-check Lua source without running it</summary>
    public static string? Compile(string source)
    {
        try
        {
            new Script(CoreModules.None).LoadString(source);
            return null;
        }
        catch (SyntaxErrorException ex) { return ex.DecoratedMessage ?? ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }
}
