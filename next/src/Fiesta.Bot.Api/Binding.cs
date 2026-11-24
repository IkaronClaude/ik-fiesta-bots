using Fiesta.Bot.Agent;

namespace Fiesta.Bot.Api;

/// <summary>Marks a class as a Lua module; the source generator emits its bindings and manifest entries.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LuaModuleAttribute(string name) : Attribute
{
    /// <summary>Lua module (global table) name.</summary>
    public string Name { get; } = name;
}

/// <summary>Marks a method as a Lua function (name = method name with a lower-case first letter unless given).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LuaFnAttribute : Attribute
{
    /// <summary>Explicit Lua name.</summary>
    public string? Name { get; init; }
    /// <summary>Cost class (API_SPEC 1.2).</summary>
    public Cost Cost { get; init; } = Cost.O1;
    /// <summary>Allocation mark (API_SPEC 1.3).</summary>
    public Alloc Alloc { get; init; } = Alloc.None;
    /// <summary>Also expose as a global alias (e.g. canUse).</summary>
    public string? GlobalAlias { get; init; }
}

/// <summary>Marks a constant exposed in the module table.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class LuaConstAttribute : Attribute
{
    /// <summary>Explicit Lua name.</summary>
    public string? Name { get; init; }
}

/// <summary>Cost classes.</summary>
public enum Cost : byte { O1, Ok, On, Cached, Job }

/// <summary>Allocation marks.</summary>
public enum Alloc : byte { None, LuaString, Iterator, Small }

/// <summary>Iterator sources the generated bindings know how to walk without building tables.</summary>
public enum IteratorKind : byte { Entities, Skills, BagSlots, ActiveQuests, AvailableQuests, Npcs, PartyMembers }

/// <summary>A lazy iterator handed to Lua: the binding pushes a cached iterator closure plus this state; each step
/// asks the module for the next item after the control value.</summary>
public readonly record struct LuaIter(IteratorKind Kind, int ScratchStart, int Count);

/// <summary>Base for modules: access to the bot's context for the current tick, and the calling layer.</summary>
public abstract class ApiModule
{
    /// <summary>The bot this VM belongs to.</summary>
    protected AgentContext Ctx { get; }
    /// <summary>Create for a bot.</summary>
    protected ApiModule(AgentContext ctx) => Ctx = ctx;
    /// <summary>Layer index of the state currently running (set by the brain runtime).</summary>
    public byte CallingLayer { get; set; }
    /// <summary>Record a pending argument error (never lua_error from managed code; see LUA_BENCH.md).</summary>
    protected void ArgError(string function, string detail) => throw new NotImplementedException();
}

/// <summary>Pending error state for one VM, checked by the runner after a state call.</summary>
public sealed class ApiErrors
{
    /// <summary>First pending error this call, null when none.</summary>
    public string? Pending { get; private set; }
    /// <summary>Set (keeps the first).</summary>
    public void Set(string error) => throw new NotImplementedException();
    /// <summary>Take and clear.</summary>
    public string? Take() => throw new NotImplementedException();
}

/// <summary>One function in the API manifest.</summary>
public sealed record ManifestFunction(string Module, string Name, string Signature, string Returns, Cost Cost, Alloc Alloc, string Doc);

/// <summary>The generated API manifest (editor completion, linter, brain `api:` check).</summary>
public sealed record ApiManifest(int ApiVersion, IReadOnlyList<ManifestFunction> Functions, IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Constants)
{
    /// <summary>Current API version.</summary>
    public const int Version = 3;
    /// <summary>Build from the module types by reflection (used until the generator emits it).</summary>
    public static ApiManifest Build() => throw new NotImplementedException();
}

/// <summary>Creates every module for a bot and registers them into a Lua engine.</summary>
public sealed class ApiRegistry
{
    /// <summary>All module instances for a bot.</summary>
    public static IReadOnlyList<ApiModule> CreateModules(AgentContext ctx) => throw new NotImplementedException();
}
