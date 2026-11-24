using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using KeraLua;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.LuaBench;

/// <summary>One Lua VM bound to one <see cref="World"/>. Every engine exposes the same global functions.</summary>
public interface IEngine : IDisposable
{
    string Name { get; }
    /// <summary>Call the global Lua function <paramref name="fn"/>(n) and return its number result</summary>
    double Call(string fn, long n);
    /// <summary>Compile and run a chunk (the hot-reload path)</summary>
    void Reload(string source);
    /// <summary>Bytes held by the Lua heap itself (native engines; managed engines report 0)</summary>
    long LuaHeapBytes();
}

public static class Engines
{
    public static readonly string[] Names = ["moonsharp-userdata", "moonsharp-callback", "keralua", "raw-fnptr"];

    public static IEngine Create(string name, World w) => name switch
    {
        "moonsharp-userdata" => new MoonSharpUserDataEngine(w),
        "moonsharp-callback" => new MoonSharpCallbackEngine(w),
        "keralua" => new KeraLuaEngine(w),
        "raw-fnptr" => new RawEngine(w),
        _ => throw new ArgumentException(name),
    };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// MoonSharp, the way BotApi is exposed today: a registered userdata type, called as bot.fn(...)
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[MoonSharpUserData]
public sealed class BotFacade
{
    private readonly World _w;
    private Script? _script;
    public BotFacade(World w) => _w = w;
    internal void Attach(Script s) => _script = s;

    public double hp() => _w.SelfHp;
    public double dist(long h) => _w.Dist(h);
    public double mob_hp(long h) => _w.MobHp(h);
    public bool can_use(long s, long t) => _w.CanUse(s, t);
    public string map() => _w.MapName;
    public void log(string s) { _w.LogCalls++; _w.LogBytes += s.Length; }

    public DynValue mob_next(DynValue state, DynValue prev)
    {
        var n = _w.Next((long)prev.Number);
        return n < 0 ? DynValue.Nil : DynValue.NewNumber(n);
    }

    /// <summary>Same shape as today's nearbyMobs(): a fresh table of tables per call</summary>
    public Table mobs_table()
    {
        var t = new Table(_script);
        for (int i = 0; i < World.MobCount; i++)
        {
            var m = new Table(_script);
            m["handle"] = i; m["x"] = _w.X[i]; m["y"] = _w.Y[i]; m["hp"] = _w.Hp[i]; m["dist"] = _w.Dist(i);
            t[i + 1] = m;
        }
        return t;
    }
}

public sealed class MoonSharpUserDataEngine : IEngine
{
    private static readonly object Gate = new();
    private static bool _registered;
    private readonly Script _s;
    private readonly Dictionary<string, DynValue> _fns = new();
    public string Name => "moonsharp-userdata";

    public MoonSharpUserDataEngine(World w)
    {
        lock (Gate) { if (!_registered) { UserData.RegisterType<BotFacade>(); _registered = true; } }
        _s = new Script(CoreModules.Preset_SoftSandbox);
        var bot = new BotFacade(w);
        bot.Attach(_s);
        _s.Globals["bot"] = bot;
        _s.DoString(Scenario.For("bot."));
    }

    public double Call(string fn, long n)
    {
        if (!_fns.TryGetValue(fn, out var f)) _fns[fn] = f = _s.Globals.Get(fn);
        return _s.Call(f, DynValue.NewNumber(n)).Number;
    }

    public void Reload(string source) => _s.DoString(source);
    public long LuaHeapBytes() => 0;
    public void Dispose() { }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// MoonSharp, best case: CallbackFunction globals (no reflection dispatch)
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class MoonSharpCallbackEngine : IEngine
{
    private readonly Script _s;
    private readonly Dictionary<string, DynValue> _fns = new();
    public string Name => "moonsharp-callback";

    public MoonSharpCallbackEngine(World w)
    {
        _s = new Script(CoreModules.Preset_SoftSandbox);
        var g = _s.Globals;
        var mapName = DynValue.NewString(w.MapName);
        g["hp"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(w.SelfHp));
        g["dist"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(w.Dist((long)a[0].Number)));
        g["mob_hp"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(w.MobHp((long)a[0].Number)));
        g["can_use"] = DynValue.NewCallback((c, a) => w.CanUse((long)a[0].Number, (long)a[1].Number) ? DynValue.True : DynValue.False);
        g["map"] = DynValue.NewCallback((c, a) => mapName);
        g["log"] = DynValue.NewCallback((c, a) => { w.LogCalls++; w.LogBytes += a[0].String.Length; return DynValue.Nil; });
        g["mob_next"] = DynValue.NewCallback((c, a) =>
        {
            var n = w.Next((long)a[1].Number);
            return n < 0 ? DynValue.Nil : DynValue.NewNumber(n);
        });
        g["mobs_table"] = DynValue.NewCallback((c, a) =>
        {
            var t = new Table(_s);
            for (int i = 0; i < World.MobCount; i++)
            {
                var m = new Table(_s);
                m["handle"] = i; m["x"] = w.X[i]; m["y"] = w.Y[i]; m["hp"] = w.Hp[i]; m["dist"] = w.Dist(i);
                t[i + 1] = m;
            }
            return DynValue.NewTable(t);
        });
        _s.DoString(Scenario.For(""));
    }

    public double Call(string fn, long n)
    {
        if (!_fns.TryGetValue(fn, out var f)) _fns[fn] = f = _s.Globals.Get(fn);
        return _s.Call(f, DynValue.NewNumber(n)).Number;
    }

    public void Reload(string source) => _s.DoString(source);
    public long LuaHeapBytes() => 0;
    public void Dispose() { }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// KeraLua (native Lua 5.4.9), idiomatic use: LuaFunction delegates, KeraLua's string helpers
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class KeraLuaEngine : IEngine
{
    private readonly KeraLua.Lua _l;
    private readonly List<LuaFunction> _keepAlive = new();
    public string Name => "keralua";

    public KeraLuaEngine(World w)
    {
        _l = new KeraLua.Lua(openLibs: true);
        void Reg(string name, LuaFunction f) { _keepAlive.Add(f); _l.Register(name, f); }
        var L = _l;
        Reg("hp", s => { L.PushNumber(w.SelfHp); return 1; });
        Reg("dist", s => { L.PushNumber(w.Dist(L.ToInteger(1))); return 1; });
        Reg("mob_hp", s => { L.PushNumber(w.MobHp(L.ToInteger(1))); return 1; });
        Reg("can_use", s => { L.PushBoolean(w.CanUse(L.ToInteger(1), L.ToInteger(2))); return 1; });
        Reg("map", s => { L.PushString(w.MapName); return 1; });
        Reg("log", s => { var str = L.ToString(1); w.LogCalls++; w.LogBytes += str.Length; return 0; });
        Reg("mob_next", s =>
        {
            var n = w.Next(L.ToInteger(2));
            if (n < 0) L.PushNil(); else L.PushInteger(n);
            return 1;
        });
        Reg("mobs_table", s =>
        {
            L.CreateTable(World.MobCount, 0);
            for (int i = 0; i < World.MobCount; i++)
            {
                L.CreateTable(0, 5);
                L.PushInteger(i); L.SetField(-2, "handle");
                L.PushNumber(w.X[i]); L.SetField(-2, "x");
                L.PushNumber(w.Y[i]); L.SetField(-2, "y");
                L.PushNumber(w.Hp[i]); L.SetField(-2, "hp");
                L.PushNumber(w.Dist(i)); L.SetField(-2, "dist");
                L.RawSetInteger(-2, i + 1);
            }
            return 1;
        });
        if (_l.DoString(Scenario.For(""))) throw new InvalidOperationException(_l.ToString(-1));
    }

    public double Call(string fn, long n)
    {
        _l.GetGlobal(fn);
        _l.PushInteger(n);
        if (_l.PCall(1, 1, 0) != LuaStatus.OK) throw new InvalidOperationException(_l.ToString(-1));
        var r = _l.ToNumber(-1);
        _l.Pop(1);
        return r;
    }

    public void Reload(string source)
    {
        if (_l.DoString(source)) throw new InvalidOperationException(_l.ToString(-1));
    }

    public long LuaHeapBytes() => (long)_l.GarbageCollector(LuaGC.Count, 0) * 1024;
    public void Dispose() => _l.Dispose();
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Minimal direct binding to the same lua54 library: [UnmanagedCallersOnly] callbacks, function pointers,
// pre-encoded UTF-8 names, no managed strings on the hot path. This is what a source generator would emit.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static unsafe partial class Lua54
{
    private const string Lib = "lua54";
    [LibraryImport(Lib), SuppressGCTransition] public static partial long lua_tointegerx(IntPtr L, int idx, int* isnum);
    [LibraryImport(Lib), SuppressGCTransition] public static partial double lua_tonumberx(IntPtr L, int idx, int* isnum);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushnumber(IntPtr L, double n);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushinteger(IntPtr L, long n);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushboolean(IntPtr L, int b);
    [LibraryImport(Lib), SuppressGCTransition] public static partial void lua_pushnil(IntPtr L);
    [LibraryImport(Lib), SuppressGCTransition] public static partial byte* lua_tolstring(IntPtr L, int idx, nuint* len);
    // these can allocate inside Lua (and so run the Lua GC), keep the normal GC transition
    [LibraryImport(Lib)] public static partial byte* lua_pushlstring(IntPtr L, byte* s, nuint len);
    [LibraryImport(Lib)] public static partial void lua_createtable(IntPtr L, int narr, int nrec);
    [LibraryImport(Lib)] public static partial void lua_setfield(IntPtr L, int idx, byte* k);
    [LibraryImport(Lib)] public static partial void lua_rawseti(IntPtr L, int idx, long n);
    [LibraryImport(Lib)] public static partial void lua_pushcclosure(IntPtr L, IntPtr fn, int n);
    [LibraryImport(Lib)] public static partial void lua_setglobal(IntPtr L, byte* name);
    [LibraryImport(Lib)] public static partial int lua_getglobal(IntPtr L, byte* name);
    [LibraryImport(Lib)] public static partial int lua_pcallk(IntPtr L, int nargs, int nresults, int errfunc, IntPtr ctx, IntPtr k);
    [LibraryImport(Lib)] public static partial void lua_settop(IntPtr L, int idx);
}

public sealed unsafe class RawEngine : IEngine
{
    // One VM per bot and one bot per tick thread at a time: the callback finds its world through a thread-static.
    [ThreadStatic] private static World? _cur;

    private static readonly byte* KHandle = Utf8("handle"), KX = Utf8("x"), KY = Utf8("y"), KHp = Utf8("hp"), KDist = Utf8("dist");
    private static readonly Dictionary<string, IntPtr> FnNames = new();
    private readonly KeraLua.Lua _l;
    private readonly World _w;
    private readonly byte* _map;
    private readonly int _mapLen;
    public string Name => "raw-fnptr";

    private static byte* Utf8(string s)
    {
        var b = Encoding.UTF8.GetBytes(s + "\0");
        var p = (byte*)NativeMemory.Alloc((nuint)b.Length);
        b.AsSpan().CopyTo(new Span<byte>(p, b.Length));
        return p;
    }

    public RawEngine(World w)
    {
        _w = w;
        _l = new KeraLua.Lua(openLibs: true);
        var L = _l.Handle;
        _map = Utf8(w.MapName); _mapLen = Encoding.UTF8.GetByteCount(w.MapName);
        void Reg(string name, delegate* unmanaged[Cdecl]<IntPtr, int> f)
        {
            Lua54.lua_pushcclosure(L, (IntPtr)f, 0);
            var n = Utf8(name);
            Lua54.lua_setglobal(L, n);
            NativeMemory.Free(n);
        }
        Reg("hp", &Hp); Reg("dist", &Dist); Reg("mob_hp", &MobHp); Reg("can_use", &CanUse);
        Reg("map", &Map); Reg("log", &Log); Reg("mob_next", &MobNext); Reg("mobs_table", &MobsTable);
        _cur = w; _mapCur = this;
        if (_l.DoString(Scenario.For(""))) throw new InvalidOperationException(_l.ToString(-1));
    }

    [ThreadStatic] private static RawEngine? _mapCur;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Hp(IntPtr L) { Lua54.lua_pushnumber(L, _cur!.SelfHp); return 1; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Dist(IntPtr L) { Lua54.lua_pushnumber(L, _cur!.Dist(Lua54.lua_tointegerx(L, 1, null))); return 1; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MobHp(IntPtr L) { Lua54.lua_pushnumber(L, _cur!.MobHp(Lua54.lua_tointegerx(L, 1, null))); return 1; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanUse(IntPtr L)
    {
        Lua54.lua_pushboolean(L, _cur!.CanUse(Lua54.lua_tointegerx(L, 1, null), Lua54.lua_tointegerx(L, 2, null)) ? 1 : 0);
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Map(IntPtr L) { var e = _mapCur!; Lua54.lua_pushlstring(L, e._map, (nuint)e._mapLen); return 1; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Log(IntPtr L)
    {
        nuint len;
        var p = Lua54.lua_tolstring(L, 1, &len);   // read the bytes in place; a real logger copies into a ring
        _cur!.LogCalls++; _cur.LogBytes += (long)len;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MobNext(IntPtr L)
    {
        var n = _cur!.Next(Lua54.lua_tointegerx(L, 2, null));
        if (n < 0) Lua54.lua_pushnil(L); else Lua54.lua_pushinteger(L, n);
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MobsTable(IntPtr L)
    {
        var w = _cur!;
        Lua54.lua_createtable(L, World.MobCount, 0);
        for (int i = 0; i < World.MobCount; i++)
        {
            Lua54.lua_createtable(L, 0, 5);
            Lua54.lua_pushinteger(L, i); Lua54.lua_setfield(L, -2, KHandle);
            Lua54.lua_pushnumber(L, w.X[i]); Lua54.lua_setfield(L, -2, KX);
            Lua54.lua_pushnumber(L, w.Y[i]); Lua54.lua_setfield(L, -2, KY);
            Lua54.lua_pushnumber(L, w.Hp[i]); Lua54.lua_setfield(L, -2, KHp);
            Lua54.lua_pushnumber(L, w.Dist(i)); Lua54.lua_setfield(L, -2, KDist);
            Lua54.lua_rawseti(L, -2, i + 1);
        }
        return 1;
    }

    public double Call(string fn, long n)
    {
        _cur = _w; _mapCur = this;
        var L = _l.Handle;
        IntPtr name;
        lock (FnNames) { if (!FnNames.TryGetValue(fn, out name)) FnNames[fn] = name = (IntPtr)Utf8(fn); }
        Lua54.lua_getglobal(L, (byte*)name);
        Lua54.lua_pushinteger(L, n);
        if (Lua54.lua_pcallk(L, 1, 1, 0, IntPtr.Zero, IntPtr.Zero) != 0) throw new InvalidOperationException(_l.ToString(-1));
        var r = Lua54.lua_tonumberx(L, -1, null);
        Lua54.lua_settop(L, -2);
        return r;
    }

    public void Reload(string source)
    {
        _cur = _w; _mapCur = this;
        if (_l.DoString(source)) throw new InvalidOperationException(_l.ToString(-1));
    }

    public long LuaHeapBytes() => (long)_l.GarbageCollector(LuaGC.Count, 0) * 1024;
    public void Dispose() { NativeMemory.Free(_map); _l.Dispose(); }
}
