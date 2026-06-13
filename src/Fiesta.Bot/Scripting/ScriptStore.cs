using System.Collections.Concurrent;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>One stored behaviour script in the library.</summary>
public sealed record StoredScript(string Name, string Source, DateTime UpdatedUtc);

/// <summary>
/// In-memory library of uploaded Lua behaviour scripts, keyed by name. "Build and
/// upload on the fly": an upsert compile-checks the source (so a syntax error is
/// rejected at upload, not at apply) and replaces any prior version. Thread-safe.
/// Persistence (disk/Git) is a deliberate follow-up — not needed to prove the loop.
/// </summary>
public sealed class ScriptStore
{
    private readonly ConcurrentDictionary<string, StoredScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compile-check and store/replace a script. Returns the parse error
    /// (null on success) — the caller surfaces it as a 400.</summary>
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

    /// <summary>Parse-check Lua source without running it. Returns the decorated parse
    /// error, or null if it compiles. Shared by upload validation and inline-apply.</summary>
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
