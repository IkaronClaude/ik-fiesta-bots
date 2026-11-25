using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

/// <summary>A lazy iterator handed to Lua: items live in the call's int arena; the binding pushes a static iterator
/// function and the arena offset as the generic-for state (no closure, no table).</summary>
public readonly record struct LuaIter(int Start, int Count);

/// <summary>A caller-owned Lua table argument (stack index), for the `fill(tbl, ...)` functions.</summary>
public readonly record struct LuaTableArg(int Index);

/// <summary>Variadic Lua arguments left on the stack; read lazily (dbg.log formats only when enabled).</summary>
public readonly unsafe struct LuaAny(IntPtr l, int first, int top)
{
    /// <summary>The VM.</summary>
    public IntPtr L { get; } = l;
    /// <summary>First argument index.</summary>
    public int First { get; } = first;
    /// <summary>Last argument index.</summary>
    public int Top { get; } = top;
    /// <summary>Count.</summary>
    public int Count => Math.Max(0, Top - First + 1);
    /// <summary>Argument i (0-based) as text (Lua's tostring rules for numbers/booleans/nil).</summary>
    public string Text(int i)
    {
        var idx = First + i;
        return Lua54.lua_type(L, idx) switch
        {
            -1 or 0 => "nil",
            1 => Lua54.lua_toboolean(L, idx) != 0 ? "true" : "false",
            3 => Lua54.lua_isinteger(L, idx) != 0 ? Lua54.lua_tointegerx(L, idx, null).ToString() : Lua54.lua_tonumberx(L, idx, null).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            4 => LuaPush.ToStr(L, idx) ?? "",
            _ => "<" + (Lua54.lua_type(L, idx) switch { 5 => "table", 6 => "function", _ => "value" }) + ">",
        };
    }
    /// <summary>Argument i as a number (NaN when not one).</summary>
    public double Number(int i) { int ok; var d = Lua54.lua_tonumberx(L, First + i, &ok); return ok != 0 ? d : double.NaN; }
}

/// <summary>Native Lua 5.4 entry points (the KeraLua package's lua54 library), called directly with function pointers
/// on the hot path. Stack accessors suppress the GC transition; calls that can allocate inside Lua keep it.</summary>
public static unsafe partial class Lua54
{
    private const string Lib = "lua54";
#pragma warning disable CS1591
    [LibraryImport(Lib), SuppressGCTransition] public static partial int lua_gettop(IntPtr L);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_settop(IntPtr L, int idx);
    [LibraryImport(Lib), SuppressGCTransition] public static partial int lua_type(IntPtr L, int idx);
    [LibraryImport(Lib), SuppressGCTransition] public static partial long lua_tointegerx(IntPtr L, int idx, int* isnum);
    [LibraryImport(Lib), SuppressGCTransition] public static partial double lua_tonumberx(IntPtr L, int idx, int* isnum);
    [LibraryImport(Lib), SuppressGCTransition] public static partial int lua_toboolean(IntPtr L, int idx);
    [LibraryImport(Lib), SuppressGCTransition] public static partial int lua_isinteger(IntPtr L, int idx);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushnil(IntPtr L);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushinteger(IntPtr L, long n);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushnumber(IntPtr L, double n);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushboolean(IntPtr L, int b);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushvalue(IntPtr L, int idx);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushlightuserdata(IntPtr L, IntPtr p);
    [LibraryImport(Lib), SuppressGCTransition] public static partial IntPtr lua_touserdata(IntPtr L, int idx);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_rotate(IntPtr L, int idx, int n);
    [LibraryImport(Lib), SuppressGCTransition] public static partial int lua_status(IntPtr L);
    [LibraryImport(Lib), SuppressGCTransition] public static partial int lua_isyieldable(IntPtr L);
    [LibraryImport(Lib)] public static partial byte* lua_tolstring(IntPtr L, int idx, nuint* len);
    [LibraryImport(Lib)] public static partial byte* lua_pushlstring(IntPtr L, byte* s, nuint len);
    [LibraryImport(Lib)] public static partial void lua_pushcclosure(IntPtr L, IntPtr fn, int n);
    [LibraryImport(Lib)] public static partial void lua_createtable(IntPtr L, int narr, int nrec);
    [LibraryImport(Lib)] public static partial void lua_setfield(IntPtr L, int idx, byte* k);
    [LibraryImport(Lib)] public static partial int lua_getfield(IntPtr L, int idx, byte* k);
    [LibraryImport(Lib)] public static partial void lua_setglobal(IntPtr L, byte* name);
    [LibraryImport(Lib)] public static partial int lua_getglobal(IntPtr L, byte* name);
    [LibraryImport(Lib)] public static partial void lua_rawseti(IntPtr L, int idx, long n);
    [LibraryImport(Lib)] public static partial int lua_rawgeti(IntPtr L, int idx, long n);
    [LibraryImport(Lib)] public static partial ulong lua_rawlen(IntPtr L, int idx);
    [LibraryImport(Lib)] public static partial void lua_settable(IntPtr L, int idx);
    [LibraryImport(Lib)] public static partial int lua_next(IntPtr L, int idx);
    [LibraryImport(Lib)] public static partial IntPtr lua_newthread(IntPtr L);
    [LibraryImport(Lib)] public static partial int lua_resume(IntPtr L, IntPtr from, int narg, int* nres);
    [LibraryImport(Lib)] public static partial int lua_closethread(IntPtr L, IntPtr from);
    [LibraryImport(Lib)] public static partial void lua_sethook(IntPtr L, IntPtr fn, int mask, int count);
    [LibraryImport(Lib)] public static partial int lua_yieldk(IntPtr L, int nresults, IntPtr ctx, IntPtr k);
    [LibraryImport(Lib)] public static partial int lua_gc(IntPtr L, int what, int arg);
    [LibraryImport(Lib)] public static partial int lua_pcallk(IntPtr L, int nargs, int nresults, int errfunc, IntPtr ctx, IntPtr k);
    [LibraryImport(Lib)] public static partial void lua_xmove(IntPtr from, IntPtr to, int n);
    [LibraryImport(Lib)] public static partial int luaL_loadbufferx(IntPtr L, byte* buff, nuint sz, byte* name, byte* mode);
    [LibraryImport(Lib)] public static partial int luaL_ref(IntPtr L, int t);
    [LibraryImport(Lib)] public static partial void luaL_unref(IntPtr L, int t, int r);
    [LibraryImport(Lib)] public static partial IntPtr luaL_newstate();
    [LibraryImport(Lib)] public static partial void lua_close(IntPtr L);
    [LibraryImport(Lib)] public static partial void luaL_requiref(IntPtr L, byte* modname, IntPtr openf, int glb);
    [LibraryImport(Lib)] public static partial int luaopen_base(IntPtr L);
    [LibraryImport(Lib)] public static partial int luaopen_string(IntPtr L);
    [LibraryImport(Lib)] public static partial int luaopen_table(IntPtr L);
    [LibraryImport(Lib)] public static partial int luaopen_math(IntPtr L);
    [LibraryImport(Lib)] public static partial int luaopen_utf8(IntPtr L);
    [LibraryImport(Lib)] public static partial int luaopen_coroutine(IntPtr L);
#pragma warning restore CS1591
    /// <summary>The registry pseudo-index.</summary>
    public const int RegistryIndex = -1001000;
    /// <summary>Count hook mask.</summary>
    public const int MaskCount = 1 << 3;
    /// <summary>Pop n values.</summary>
    public static void Pop(IntPtr L, int n) => lua_settop(L, -n - 1);
}

/// <summary>Push / read helpers used by the generated bindings (no managed allocation for pushes).</summary>
public static unsafe class LuaPush
{
    /// <summary>Push a string (UTF-8 encoded into a stack buffer; Lua copies and interns short strings).</summary>
    public static void Str(IntPtr L, string? s)
    {
        if (s is null) { Lua54.lua_pushnil(L); return; }
        var max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (max <= 512)
        {
            byte* b = stackalloc byte[max];
            var n = Encoding.UTF8.GetBytes(s, new Span<byte>(b, max));
            Lua54.lua_pushlstring(L, b, (nuint)n);
            return;
        }
        var rent = ArrayPool<byte>.Shared.Rent(max);
        var len = Encoding.UTF8.GetBytes(s, rent);
        fixed (byte* p = rent) Lua54.lua_pushlstring(L, p, (nuint)len);
        ArrayPool<byte>.Shared.Return(rent);
    }

    [ThreadStatic] private static (IntPtr Ptr, int Len, string Value)[]? _intern;

    /// <summary>Read a string argument. Lua interns short strings, so a repeated argument (a watch name, a format
    /// string, a map name) comes back at the same address: a small per-thread cache keyed by address and length,
    /// verified by content, returns the same managed string without allocating.</summary>
    public static string? ToStr(IntPtr L, int idx)
    {
        nuint len;
        var p = Lua54.lua_tolstring(L, idx, &len);
        if (p == null) return null;
        var n = (int)len;
        if (n > 64) return Encoding.UTF8.GetString(p, n);
        var cache = _intern ??= new (IntPtr, int, string)[256];
        var slot = (int)(((ulong)(nint)p >> 4) & 255);
        ref var e = ref cache[slot];
        var bytes = new ReadOnlySpan<byte>(p, n);
        if (e.Ptr == (IntPtr)p && e.Len == n && e.Value is { } v && Same(bytes, v)) return v;
        var s = Encoding.UTF8.GetString(p, n);
        e = ((IntPtr)p, n, s);
        return s;
    }

    private static bool Same(ReadOnlySpan<byte> b, string s)
    {
        if (b.Length != s.Length) return false;
        for (var i = 0; i < b.Length; i++) if (b[i] != s[i]) return false;
        return true;
    }

    /// <summary>Push the iterator triple (next function, state = arena offset, control = nil).</summary>
    public static int Iter(IntPtr L, LuaIter it)
    {
        Lua54.lua_pushcclosure(L, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&IterNext, 0);
        Lua54.lua_pushinteger(L, it.Start);
        Lua54.lua_pushnil(L);
        return 3;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IterNext(IntPtr L)
    {
        var start = (int)Lua54.lua_tointegerx(L, 1, null);
        var arena = ApiScope.Arena;
        if (arena is null || start < 2 || start > arena.Length) { Lua54.lua_pushnil(L); return 1; }
        var count = arena[start - 2]; var cursor = arena[start - 1];
        if (cursor >= count) { Lua54.lua_pushnil(L); return 1; }
        arena[start - 1] = cursor + 1;
        Lua54.lua_pushinteger(L, arena[start + cursor]);
        return 1;
    }

    /// <summary>Write iterator items into a caller-owned table (1..n), clear the rest, push n.</summary>
    public static int Fill(IntPtr L, int tableIdx, LuaIter it)
    {
        var arena = ApiScope.Arena!;
        for (var i = 0; i < it.Count; i++) { Lua54.lua_pushinteger(L, arena[it.Start + i]); Lua54.lua_rawseti(L, tableIdx, i + 1); }
        var old = (long)Lua54.lua_rawlen(L, tableIdx);
        for (var i = it.Count + 1; i <= old; i++) { Lua54.lua_pushnil(L); Lua54.lua_rawseti(L, tableIdx, i); }
        Lua54.lua_pushinteger(L, it.Count);
        return 1;
    }
}

/// <summary>Multiple return values of mixed kinds (rare: ev.last, quests.objective).</summary>
public readonly struct LuaMulti(long a, long b, long c, long d, int n)
{
    /// <summary>Push the values as integers (-1 entries are pushed as given).</summary>
    public int Push(IntPtr L)
    {
        Lua54.lua_pushinteger(L, a);
        if (n > 1) Lua54.lua_pushinteger(L, b);
        if (n > 2) Lua54.lua_pushinteger(L, c);
        if (n > 3) Lua54.lua_pushinteger(L, d);
        return n;
    }
}

/// <summary>The per-thread binding scope: which bot's modules the generated functions dispatch to, the iterator arena
/// and the pending error. The brain runtime binds it before each call into Lua (one bot per tick thread at a time).</summary>
public static class ApiScope
{
    [ThreadStatic] private static ApiModule[]? _modules;
    [ThreadStatic] private static int[]? _arena;
    [ThreadStatic] private static int _top;
    [ThreadStatic] private static ApiErrors? _errors;
    [ThreadStatic] private static byte _layer;
    /// <summary>The bound modules.</summary>
    public static ApiModule[]? Modules => _modules;
    /// <summary>The iterator arena.</summary>
    public static int[]? Arena => _arena;
    /// <summary>The calling layer index.</summary>
    public static byte Layer => _layer;

    /// <summary>Bind a bot's modules for the calls that follow (resets the arena).</summary>
    public static void Bind(ApiModule[] modules, int[] arena, ApiErrors errors, byte layer)
    {
        _modules = modules; _arena = arena; _errors = errors; _top = 2; _layer = layer;
    }

    /// <summary>Reset the iterator arena (start of a Lua call).</summary>
    public static void ResetArena() => _top = 2;

    /// <summary>Reserve an iterator region and copy items in.</summary>
    public static LuaIter Iter(ReadOnlySpan<int> items)
    {
        var a = _arena!;
        var start = _top + 2;
        if (start + items.Length > a.Length) return new LuaIter(0, 0);
        a[start - 2] = items.Length; a[start - 1] = 0;
        items.CopyTo(a.AsSpan(start));
        _top = start + items.Length;
        return new LuaIter(start, items.Length);
    }

    /// <summary>Record an argument error (never lua_error from managed code).</summary>
    public static void ArgError(string fn, string detail) => _errors?.Set($"E_ARG: {fn}: {detail}");
}

/// <summary>Base for modules: access to the bot's context for the current tick.</summary>
public abstract class ApiModule
{
    /// <summary>The bot this VM belongs to.</summary>
    protected AgentContext Ctx { get; }
    /// <summary>Create for a bot.</summary>
    protected ApiModule(AgentContext ctx) => Ctx = ctx;
    /// <summary>Layer index of the state currently running.</summary>
    protected static byte CallingLayer => ApiScope.Layer;
    /// <summary>Record a pending argument error.</summary>
    protected static void ArgError(string function, string detail) => ApiScope.ArgError(function, detail);
}

/// <summary>Pending error state for one VM, checked by the runner after a call.</summary>
public sealed class ApiErrors
{
    /// <summary>First pending error, null when none.</summary>
    public string? Pending { get; private set; }
    /// <summary>Set (keeps the first).</summary>
    public void Set(string error) => Pending ??= error;
    /// <summary>Take and clear.</summary>
    public string? Take() { var p = Pending; Pending = null; return p; }
}

/// <summary>Registers functions into module tables (created by the engine; the generator calls Fn for each).</summary>
public sealed unsafe class LuaRegistrar
{
    private readonly List<(string Alias, string Module, string Fn)> _aliases = new();
    private string _module = "";

    /// <summary>Start a module table (pushes it).</summary>
    public void BeginModule(IntPtr L, string name)
    {
        _module = name;
        Lua54.lua_createtable(L, 0, 32);
    }
    /// <summary>Register one function into the table on top.</summary>
    public void Fn(IntPtr L, string name, IntPtr fn)
    {
        Lua54.lua_pushcclosure(L, fn, 0);
        SetField(L, -2, name);
    }
    /// <summary>Remember a global alias.</summary>
    public void Alias(string alias, string fn) => _aliases.Add((alias, _module, fn));
    /// <summary>Finish: set constants, then the global.</summary>
    public void EndModule(IntPtr L, string name)
    {
        if (LuaConstants.For(name) is { } consts)
            foreach (var kv in consts) { Lua54.lua_pushinteger(L, kv.Value); SetField(L, -2, kv.Key); }
        SetGlobal(L, name);
    }
    /// <summary>Install the global aliases (after all modules).</summary>
    public void InstallAliases(IntPtr L)
    {
        foreach (var (alias, module, fn) in _aliases)
        {
            GetGlobal(L, module);
            GetField(L, -1, fn);
            SetGlobal(L, alias);
            Lua54.Pop(L, 1);
        }
    }

    /// <summary>t[k] = top, popping it.</summary>
    public static void SetField(IntPtr L, int idx, string k) { fixed (byte* p = Z(k)) Lua54.lua_setfield(L, idx, p); }
    /// <summary>Push t[k].</summary>
    public static int GetField(IntPtr L, int idx, string k) { fixed (byte* p = Z(k)) return Lua54.lua_getfield(L, idx, p); }
    /// <summary>Global = top, popping it.</summary>
    public static void SetGlobal(IntPtr L, string k) { fixed (byte* p = Z(k)) Lua54.lua_setglobal(L, p); }
    /// <summary>Push a global.</summary>
    public static int GetGlobal(IntPtr L, string k) { fixed (byte* p = Z(k)) return Lua54.lua_getglobal(L, p); }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> ZCache = new();
    /// <summary>Zero-terminated UTF-8 (cached per string: keys are few and reused).</summary>
    public static byte[] Z(string s)
    {
        if (ZCache.TryGetValue(s, out var c)) return c;
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1]; Encoding.UTF8.GetBytes(s, b);
        if (ZCache.Count < 4096) ZCache[s] = b;
        return b;
    }
}

/// <summary>One function in the API manifest.</summary>
public sealed record ManifestFunction(string Module, string Name, string Signature, string Returns, Cost Cost, Alloc Alloc, string Doc);

/// <summary>The API manifest (editor completion, linter, brain `api:` check): the generated function list plus the
/// constants and events.</summary>
public static class ApiManifest
{
    /// <summary>Current API version.</summary>
    public const int Version = 3;
    private static string? _json;
    private static HashSet<string>? _names;

    /// <summary>The full manifest JSON (functions from the generator, constants, events, sensors).</summary>
    public static string Json(IEnumerable<string>? sensors = null)
    {
        if (_json is not null && sensors is null) return _json;
        var fns = System.Text.Json.Nodes.JsonNode.Parse(GeneratedApi.ManifestJson)!.AsObject();
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["apiVersion"] = Version,
            ["functionCount"] = GeneratedApi.FunctionCount,
            ["modules"] = fns["modules"]!.DeepClone(),
            ["constants"] = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(LuaConstants.All())),
            ["events"] = new System.Text.Json.Nodes.JsonArray(Enum.GetNames<EventKind>().Select(n => (System.Text.Json.Nodes.JsonNode?)LuaConstants.Upper(n)).ToArray()),
            ["resources"] = new System.Text.Json.Nodes.JsonArray(Enum.GetNames<Resource>().Where(n => n is not "None" and not "All").Select(n => (System.Text.Json.Nodes.JsonNode?)n).ToArray()),
            ["globals"] = new System.Text.Json.Nodes.JsonObject { ["canUse"] = "skills.canUse", ["log"] = "dbg.log" },
        };
        if (sensors is not null) root["sensors"] = new System.Text.Json.Nodes.JsonArray(sensors.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray());
        var j = root.ToJsonString();
        if (sensors is null) _json = j;
        return j;
    }

    /// <summary>All "module.fn" names (linter).</summary>
    public static IReadOnlySet<string> Names()
    {
        if (_names is not null) return _names;
        var s = new HashSet<string>();
        var doc = System.Text.Json.JsonDocument.Parse(GeneratedApi.ManifestJson);
        foreach (var m in doc.RootElement.GetProperty("modules").EnumerateArray())
        {
            var mn = m.GetProperty("name").GetString()!;
            foreach (var f in m.GetProperty("functions").EnumerateArray()) s.Add(mn + "." + f.GetProperty("name").GetString());
        }
        return _names = s;
    }

    /// <summary>Zero-argument numeric functions (the default sensors for `value:` conditions).</summary>
    public static IReadOnlyList<string> ZeroArgNumeric()
    {
        var o = new List<string>();
        var doc = System.Text.Json.JsonDocument.Parse(GeneratedApi.ManifestJson);
        foreach (var m in doc.RootElement.GetProperty("modules").EnumerateArray())
            foreach (var f in m.GetProperty("functions").EnumerateArray())
            {
                var ps = f.GetProperty("params");
                var required = ps.EnumerateArray().Count(p => !p.TryGetProperty("default", out _));
                var ret = f.GetProperty("returns").GetString();
                if (required == 0 && ret is "int" or "long" or "double" or "bool")
                    o.Add(m.GetProperty("name").GetString() + "." + f.GetProperty("name").GetString());
            }
        return o;
    }
}

/// <summary>Creates every module for a bot.</summary>
public static class ApiRegistry
{
    /// <summary>All module instances for a bot, in generated index order.</summary>
    public static ApiModule[] CreateModules(AgentContext ctx) => GeneratedApi.Create(ctx);
}
