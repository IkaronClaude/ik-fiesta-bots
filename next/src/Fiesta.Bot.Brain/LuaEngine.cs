using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fiesta.Bot.Api;

namespace Fiesta.Bot.Brain;

/// <summary>A compiled Lua function reference inside one VM (registry index).</summary>
public readonly record struct LuaRef(int Value)
{
    /// <summary>No function.</summary>
    public static readonly LuaRef None = new(-1);
    /// <summary>Valid.</summary>
    public bool Ok => Value > 0;
}

/// <summary>Outcome of one protected Lua call.</summary>
public enum LuaCallStatus : byte { Ok, Error, Budget, ArgError }

/// <summary>Result of a protected call: status, truthiness and number of the first result, error text.</summary>
public readonly record struct LuaCallResult(LuaCallStatus Status, bool Truthy, double Number, string? Error, bool IsNumber = false);

/// <summary>One Lua VM for one bot.</summary>
public interface ILuaEngine : IDisposable
{
    /// <summary>Engine name.</summary>
    string Name { get; }
    /// <summary>Register all API modules (generated bindings) and constants.</summary>
    void RegisterApi(ApiModule[] modules, ApiErrors errors);
    /// <summary>Set the module sources `require` may load (pinned by the brain version).</summary>
    void SetSources(IReadOnlyDictionary<string, string> sources);
    /// <summary>Compile and run a module chunk; returns its table as a reference, or an error.</summary>
    (LuaRef Module, string? Error) LoadModule(string path);
    /// <summary>Resolve `module.fn` (or compile an inline expression/body) to a function reference.</summary>
    (LuaRef Fn, string? Error) Resolve(string fnRefOrExpression);
    /// <summary>Call a function inside a coroutine with a wall-clock budget (ms).</summary>
    LuaCallResult Call(LuaRef fn, double budgetMs);
    /// <summary>Lua heap size in bytes.</summary>
    long HeapBytes { get; }
    /// <summary>Instructions executed by the last call (1,000-instruction resolution).</summary>
    long LastInstructions { get; }
}

/// <summary>Native Lua 5.4 (the KeraLua package's library) with the generated function-pointer bindings. Sandbox:
/// base (without load / loadfile / dofile), string, table, math, utf8, coroutine; `require` resolves only within
/// the brain version's module set. State code runs inside a coroutine; a count hook yields it when the wall-clock
/// budget is exceeded (a yield from a hook does not longjmp across managed frames).</summary>
public sealed unsafe class NativeLuaEngine : ILuaEngine
{
    private readonly KeraLua.Lua _lua;
    private readonly IntPtr L;
    private IntPtr _co;
    private int _coRef;
    private IReadOnlyDictionary<string, string> _sources = new Dictionary<string, string>();
    private readonly Dictionary<string, int> _loaded = new();
    private readonly Dictionary<string, int> _byShort = new();
    private ApiErrors _errors = new();
    private int _bbRef = -1;
    private readonly Dictionary<string, (int Layer, int State)> _bbScopes = new();
    private long _lastInstr;
    [ThreadStatic] private static NativeLuaEngine? _current;
    [ThreadStatic] private static long _hookTicks, _deadline;
    /// <summary>Hook granularity (instructions).</summary>
    public const int HookEvery = 1000;

    /// <inheritdoc/>
    public string Name => "lua54";

    /// <summary>Create a sandboxed VM.</summary>
    public NativeLuaEngine()
    {
        _lua = new KeraLua.Lua(openLibs: false);
        L = _lua.Handle;
        Open("_G", &Lua54Open.Base);
        Open("string", &Lua54Open.String);
        Open("table", &Lua54Open.Table);
        Open("math", &Lua54Open.Math);
        Open("utf8", &Lua54Open.Utf8);
        Open("coroutine", &Lua54Open.Coroutine);
        foreach (var g in new[] { "load", "loadfile", "dofile", "collectgarbage", "print" })
        {
            Lua54.lua_pushnil(L);
            LuaRegistrar.SetGlobal(L, g);
        }
        Lua54.lua_pushcclosure(L, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Require, 0);
        LuaRegistrar.SetGlobal(L, "require");
        Lua54.lua_pushcclosure(L, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Print, 0);
        LuaRegistrar.SetGlobal(L, "print");
        NewCoroutine();
    }

    private void Open(string name, delegate* unmanaged[Cdecl]<IntPtr, int> fn)
    {
        fixed (byte* p = LuaRegistrar.Z(name)) Lua54.luaL_requiref(L, p, (IntPtr)fn, 1);
        Lua54.Pop(L, 1);
    }

    private void NewCoroutine()
    {
        if (_coRef > 0) Lua54.luaL_unref(L, Lua54.RegistryIndex, _coRef);
        _co = Lua54.lua_newthread(L);
        _coRef = Lua54.luaL_ref(L, Lua54.RegistryIndex);
        Lua54.lua_sethook(_co, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&Hook, Lua54.MaskCount, HookEvery);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Hook(IntPtr l, IntPtr ar)
    {
        _hookTicks++;
        if (Stopwatch.GetTimestamp() > _deadline && Lua54.lua_isyieldable(l) != 0) Lua54.lua_yieldk(l, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Print(IntPtr l) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Require(IntPtr l)
    {
        var e = _current;
        var name = LuaPush.ToStr(l, 1);
        if (e is null || name is null) { Lua54.lua_pushnil(l); return 1; }
        var path = BrainLinter.StripPin(name);
        if (e._loaded.TryGetValue(path, out var r)) { Lua54.lua_rawgeti(l, Lua54.RegistryIndex, r); return 1; }
        if (!e._sources.TryGetValue(path, out var src) && !BuiltinLibraries.Sources.TryGetValue(path, out src))
        {
            e._errors.Set($"require: module '{path}' is not in this brain version's module set");
            Lua54.lua_pushnil(l); return 1;
        }
        if (e.Compile(l, path, src) is { } err) { e._errors.Set(err); Lua54.lua_pushnil(l); return 1; }
        if (Lua54.lua_pcallk(l, 0, 1, 0, IntPtr.Zero, IntPtr.Zero) != 0)
        {
            e._errors.Set(LuaPush.ToStr(l, -1) ?? "module error");
            Lua54.Pop(l, 1); Lua54.lua_pushnil(l); return 1;
        }
        Lua54.lua_pushvalue(l, -1);
        var reg = Lua54.luaL_ref(l, Lua54.RegistryIndex);
        e._loaded[path] = reg;
        e._byShort[BrainLinter.ShortName(path)] = reg;
        return 1;
    }

    private string? Compile(IntPtr l, string chunk, string source)
    {
        var b = Encoding.UTF8.GetBytes(source);
        fixed (byte* p = b) fixed (byte* n = LuaRegistrar.Z("@" + chunk)) fixed (byte* mode = LuaRegistrar.Z("t"))
            if (Lua54.luaL_loadbufferx(l, p, (nuint)b.Length, n, mode) != 0)
            {
                var err = LuaPush.ToStr(l, -1);
                Lua54.Pop(l, 1);
                return err ?? "compile error";
            }
        return null;
    }

    /// <inheritdoc/>
    public void RegisterApi(ApiModule[] modules, ApiErrors errors)
    {
        _errors = errors;
        var reg = new LuaRegistrar();
        GeneratedApi.Register(L, reg);
        reg.InstallAliases(L);
        // the blackboard: bb = {bot = {}, layer = {}, state = {}} (layer/state are swapped per calling layer)
        Lua54.lua_createtable(L, 0, 3);
        Lua54.lua_createtable(L, 0, 8); LuaRegistrar.SetField(L, -2, "bot");
        Lua54.lua_createtable(L, 0, 8); LuaRegistrar.SetField(L, -2, "layer");
        Lua54.lua_createtable(L, 0, 8); LuaRegistrar.SetField(L, -2, "state");
        Lua54.lua_pushvalue(L, -1);
        _bbRef = Lua54.luaL_ref(L, Lua54.RegistryIndex);
        LuaRegistrar.SetGlobal(L, "bb");
    }

    /// <inheritdoc/>
    public void SetSources(IReadOnlyDictionary<string, string> sources) => _sources = sources;

    /// <inheritdoc/>
    public (LuaRef Module, string? Error) LoadModule(string path)
    {
        path = BrainLinter.StripPin(path);
        if (_loaded.TryGetValue(path, out var r)) return (new LuaRef(r), null);
        if (!_sources.TryGetValue(path, out var src) && !BuiltinLibraries.Sources.TryGetValue(path, out src)) return (LuaRef.None, $"module '{path}' not found");
        if (Compile(L, path, src) is { } err) return (LuaRef.None, err);
        var fn = Lua54.luaL_ref(L, Lua54.RegistryIndex);
        var res = CallKeep(new LuaRef(fn), 200);
        Lua54.luaL_unref(L, Lua54.RegistryIndex, fn);
        if (res.Status != LuaCallStatus.Ok) return (LuaRef.None, $"{path}: {res.Error}");
        if (Lua54.lua_type(L, -1) != 5) { Lua54.Pop(L, 1); return (LuaRef.None, $"{path}: a module must return a table"); }
        var mref = Lua54.luaL_ref(L, Lua54.RegistryIndex);
        _loaded[path] = mref;
        var shortName = BrainLinter.ShortName(path);
        _byShort[shortName] = mref;
        // reachable by its short name in conditions and state functions
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, mref);
        LuaRegistrar.SetGlobal(L, shortName);
        return (new LuaRef(mref), null);
    }

    /// <inheritdoc/>
    public (LuaRef Fn, string? Error) Resolve(string s)
    {
        s = s.Trim();
        if (BrainLinter.IsRef(s) && s.Contains('.'))
        {
            var dot = s.LastIndexOf('.');
            var mod = s[..dot]; var fn = s[(dot + 1)..];
            if (_byShort.TryGetValue(mod, out var mref))
            {
                Lua54.lua_rawgeti(L, Lua54.RegistryIndex, mref);
                LuaRegistrar.GetField(L, -1, fn);
                if (Lua54.lua_type(L, -1) == 6) { var r = Lua54.luaL_ref(L, Lua54.RegistryIndex); Lua54.Pop(L, 1); return (new LuaRef(r), null); }
                Lua54.Pop(L, 2);
                return (LuaRef.None, $"'{s}': module '{mod}' has no function '{fn}'");
            }
        }
        // an expression (a bare function path is called: `when: survival.lowHp` / `outmatched()` / `self.hpPct() < 35`)
        var code = BrainLinter.IsRef(s) ? $"local f = {s}; if type(f) == 'function' then return f() end; return f" : $"return ({s})";
        if (Compile(L, "inline", code) is { } err)
        {
            if (Compile(L, "inline", s) is { } err2) return (LuaRef.None, err);
        }
        return (new LuaRef(Lua54.luaL_ref(L, Lua54.RegistryIndex)), null);
    }

    /// <summary>Point bb.layer / bb.state at a layer's tables (creating them on first use).</summary>
    public void BindLayer(string layer)
    {
        if (_bbRef < 0) return;
        if (!_bbScopes.TryGetValue(layer, out var refs))
        {
            Lua54.lua_createtable(L, 0, 8); var lr = Lua54.luaL_ref(L, Lua54.RegistryIndex);
            Lua54.lua_createtable(L, 0, 8); var sr = Lua54.luaL_ref(L, Lua54.RegistryIndex);
            _bbScopes[layer] = refs = (lr, sr);
        }
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, _bbRef);
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, refs.Layer); LuaRegistrar.SetField(L, -2, "layer");
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, refs.State); LuaRegistrar.SetField(L, -2, "state");
        Lua54.Pop(L, 1);
    }

    /// <summary>Clear a layer's state scope (on state exit).</summary>
    public void ClearState(string layer)
    {
        if (!_bbScopes.TryGetValue(layer, out var refs)) return;
        Lua54.luaL_unref(L, Lua54.RegistryIndex, refs.State);
        Lua54.lua_createtable(L, 0, 8);
        _bbScopes[layer] = (refs.Layer, Lua54.luaL_ref(L, Lua54.RegistryIndex));
        BindLayer(layer);
    }

    /// <summary>Clear a layer's layer scope (machine change).</summary>
    public void ClearLayer(string layer)
    {
        if (!_bbScopes.TryGetValue(layer, out var refs)) return;
        Lua54.luaL_unref(L, Lua54.RegistryIndex, refs.Layer);
        Lua54.lua_createtable(L, 0, 8);
        _bbScopes[layer] = (Lua54.luaL_ref(L, Lua54.RegistryIndex), refs.State);
    }

    private int ScopeRef(string scope, string? owner)
        => scope switch
        {
            "bot" => BotRef(),
            "layer" when owner is not null => (BindLayerRefs(owner)).Layer,
            "state" when owner is not null => (BindLayerRefs(owner)).State,
            _ => -1,
        };

    private (int Layer, int State) BindLayerRefs(string owner) { if (!_bbScopes.ContainsKey(owner)) BindLayer(owner); return _bbScopes[owner]; }

    private int BotRef()
    {
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, _bbRef);
        LuaRegistrar.GetField(L, -1, "bot");
        Lua54.lua_pushvalue(L, -1);
        var r = Lua54.luaL_ref(L, Lua54.RegistryIndex);   // a short-lived ref; callers release it
        Lua54.Pop(L, 2);
        return -r;   // negative = must unref
    }

    /// <summary>Read a number from a blackboard scope (bb.state[layer][key]); NaN when absent.</summary>
    public double GetNumber(string scope, string? owner, string key)
    {
        var r = ScopeRef(scope, owner);
        if (r == -1) return double.NaN;
        var real = Math.Abs(r);
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, real);
        LuaRegistrar.GetField(L, -1, key);
        int ok; var v = Lua54.lua_tonumberx(L, -1, &ok);
        Lua54.Pop(L, 2);
        if (r < 0) Lua54.luaL_unref(L, Lua54.RegistryIndex, real);
        return ok != 0 ? v : double.NaN;
    }

    /// <summary>Does a scope hold a non-nil value for the key?</summary>
    public bool Has(string scope, string? owner, string key)
    {
        var r = ScopeRef(scope, owner);
        if (r == -1) return false;
        var real = Math.Abs(r);
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, real);
        var t = LuaRegistrar.GetField(L, -1, key);
        Lua54.Pop(L, 2);
        if (r < 0) Lua54.luaL_unref(L, Lua54.RegistryIndex, real);
        return t > 0;
    }

    /// <summary>Set a value in a scope from JSON text.</summary>
    public void Set(string scope, string? owner, string key, string json)
    {
        var r = ScopeRef(scope, owner);
        if (r == -1) throw new ArgumentException($"scope '{scope}' needs an owner layer");
        var real = Math.Abs(r);
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, real);
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch (JsonException) { node = JsonValue.Create(json); }
        Push(node, 0);
        LuaRegistrar.SetField(L, -2, key);
        Lua54.Pop(L, 1);
        if (r < 0) Lua54.luaL_unref(L, Lua54.RegistryIndex, real);
    }

    private void Push(JsonNode? n, int depth)
    {
        switch (n)
        {
            case null: Lua54.lua_pushnil(L); break;
            case JsonObject o:
                Lua54.lua_createtable(L, 0, o.Count);
                // integer keys were written as their decimal text (JSON has string keys only): restore them as integers,
                // or `t[20000]` misses after a resume / hot swap (skip lists, refused books, unstorable items...)
                if (depth < 8)
                    foreach (var kv in o)
                    {
                        if (long.TryParse(kv.Key, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ik) && ik.ToString(System.Globalization.CultureInfo.InvariantCulture) == kv.Key)
                        {
                            Lua54.lua_pushinteger(L, ik); Push(kv.Value, depth + 1); Lua54.lua_settable(L, -3);
                        }
                        else { Push(kv.Value, depth + 1); LuaRegistrar.SetField(L, -2, kv.Key); }
                    }
                break;
            case JsonArray a:
                Lua54.lua_createtable(L, a.Count, 0);
                if (depth < 8) for (var i = 0; i < a.Count; i++) { Push(a[i], depth + 1); Lua54.lua_rawseti(L, -2, i + 1); }
                break;
            case JsonValue v:
                if (v.TryGetValue<bool>(out var b)) Lua54.lua_pushboolean(L, b ? 1 : 0);
                else if (v.TryGetValue<long>(out var i)) Lua54.lua_pushinteger(L, i);
                else if (v.TryGetValue<double>(out var d)) Lua54.lua_pushnumber(L, d);
                else LuaPush.Str(L, v.ToString());
                break;
        }
    }

    /// <summary>Snapshot a scope to JSON (debugger, checkpoints).</summary>
    public JsonNode Snapshot(string scope, string? owner, int maxBytes = 4096)
    {
        var r = ScopeRef(scope, owner);
        if (r == -1) return new JsonObject();
        var real = Math.Abs(r);
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, real);
        var budget = maxBytes;
        var node = ToJson(-1, 0, ref budget) ?? new JsonObject();
        Lua54.Pop(L, 1);
        if (r < 0) Lua54.luaL_unref(L, Lua54.RegistryIndex, real);
        return node;
    }

    /// <summary>All layer names with blackboard tables.</summary>
    public IEnumerable<string> ScopeOwners => _bbScopes.Keys;

    private JsonNode? ToJson(int idx, int depth, ref int budget)
    {
        var t = Lua54.lua_type(L, idx);
        switch (t)
        {
            case 1: budget -= 5; return JsonValue.Create(Lua54.lua_toboolean(L, idx) != 0);
            case 3:
                budget -= 8;
                if (Lua54.lua_isinteger(L, idx) != 0) return JsonValue.Create(Lua54.lua_tointegerx(L, idx, null));
                // math.huge / nan have no JSON number: the largest finite double keeps 'above everything' through a
                // checkpoint (an operator survey request scores math.huge; its checkpoint failed and the apply answered 400)
                var dv = Lua54.lua_tonumberx(L, idx, null);
                return JsonValue.Create(double.IsNaN(dv) ? 0 : double.IsPositiveInfinity(dv) ? double.MaxValue : double.IsNegativeInfinity(dv) ? double.MinValue : dv);
            case 4: { var s = LuaPush.ToStr(L, idx) ?? ""; budget -= s.Length + 2; return JsonValue.Create(s); }
            case 5:
            {
                if (depth > 4 || budget <= 0) return JsonValue.Create("<table>");
                var o = new JsonObject();
                var abs = idx < 0 ? Lua54.lua_gettop(L) + idx + 1 : idx;
                Lua54.lua_pushnil(L);
                while (Lua54.lua_next(L, abs) != 0)
                {
                    string key;
                    if (Lua54.lua_type(L, -2) == 4) key = LuaPush.ToStr(L, -2) ?? "";
                    else if (Lua54.lua_type(L, -2) == 3) key = Lua54.lua_tointegerx(L, -2, null).ToString();
                    else { Lua54.Pop(L, 1); continue; }
                    if (budget > 0) o[key] = ToJson(-1, depth + 1, ref budget);
                    Lua54.Pop(L, 1);
                }
                return o;
            }
            case 0: return null;
            default: budget -= 10; return JsonValue.Create($"<{(t == 6 ? "function" : "value")}>");
        }
    }

    /// <summary>Replace the bot scope from a JSON object (checkpoint resume / hot swap).</summary>
    public void RestoreBot(JsonObject bot)
    {
        Lua54.lua_rawgeti(L, Lua54.RegistryIndex, _bbRef);
        Push(bot, 0);
        LuaRegistrar.SetField(L, -2, "bot");
        Lua54.Pop(L, 1);
    }

    /// <inheritdoc/>
    public LuaCallResult Call(LuaRef fn, double budgetMs)
    {
        var r = CallKeep(fn, budgetMs);
        if (r.Status == LuaCallStatus.Ok) Lua54.Pop(L, 1);
        return r;
    }

    // leaves the first result on the main stack on success
    private LuaCallResult CallKeep(LuaRef fn, double budgetMs)
    {
        _current = this;
        ApiScope.ResetArena();
        if (Lua54.lua_status(_co) != 0 || Lua54.lua_gettop(_co) != 0) NewCoroutine();
        Lua54.lua_rawgeti(_co, Lua54.RegistryIndex, fn.Value);
        _hookTicks = 0;
        _deadline = Stopwatch.GetTimestamp() + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
        int nres;
        var status = Lua54.lua_resume(_co, L, 0, &nres);
        _lastInstr = _hookTicks * HookEvery;
        if (status == 1)   // yielded by the hook: budget
        {
            Lua54.lua_closethread(_co, L);
            Lua54.lua_settop(_co, 0);
            _errors.Take();
            return new LuaCallResult(LuaCallStatus.Budget, false, 0, $"E_BUDGET: over {budgetMs:0.#} ms");
        }
        if (status != 0)
        {
            var err = LuaPush.ToStr(_co, -1) ?? "error";
            Lua54.lua_closethread(_co, L);
            Lua54.lua_settop(_co, 0);
            _errors.Take();
            return new LuaCallResult(LuaCallStatus.Error, false, 0, err);
        }
        bool truthy = false; double num = 0; var isNum = false;
        if (nres > 0)
        {
            var top = -nres;
            truthy = Lua54.lua_toboolean(_co, top) != 0;
            int ok; num = Lua54.lua_tonumberx(_co, top, &ok); isNum = ok != 0 && Lua54.lua_type(_co, top) == 3;
            Lua54.lua_pushvalue(_co, top);
            Lua54.lua_xmove(_co, L, 1);
        }
        else Lua54.lua_pushnil(L);
        Lua54.lua_settop(_co, 0);
        if (_errors.Take() is { } argErr)
        {
            Lua54.Pop(L, 1);
            return new LuaCallResult(LuaCallStatus.ArgError, truthy, num, argErr, isNum);
        }
        return new LuaCallResult(LuaCallStatus.Ok, truthy, num, null, isNum);
    }

    /// <inheritdoc/>
    public long HeapBytes => (long)Lua54.lua_gc(L, 3, 0) * 1024 + Lua54.lua_gc(L, 4, 0);
    /// <inheritdoc/>
    public long LastInstructions => _lastInstr;
    /// <summary>The raw state (tests, benchmarks).</summary>
    public IntPtr State => L;
    /// <inheritdoc/>
    public void Dispose() => _lua.Dispose();
}

/// <summary>Native library openers (function pointers for luaL_requiref).</summary>
internal static unsafe class Lua54Open
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int Base(IntPtr L) => Lua54.luaopen_base(L);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int String(IntPtr L) => Lua54.luaopen_string(L);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int Table(IntPtr L) => Lua54.luaopen_table(L);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int Math(IntPtr L) => Lua54.luaopen_math(L);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int Utf8(IntPtr L) => Lua54.luaopen_utf8(L);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] public static int Coroutine(IntPtr L) => Lua54.luaopen_coroutine(L);
}
