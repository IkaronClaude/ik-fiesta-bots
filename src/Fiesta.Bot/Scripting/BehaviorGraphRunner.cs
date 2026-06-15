using System.Collections.Concurrent;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Session;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>Runs a <see cref="BehaviorGraph"/> for one bot on a dedicated thread (one Lua VM,
/// single-threaded message pump — same model as <see cref="BotScriptRunner"/>).
///
/// <para>Each state and each transition is loaded into its OWN Lua environment table, so their
/// globals (<c>tick</c>, <c>on_enter</c>, locals, …) never collide; all envs share <c>bot</c>,
/// <c>log</c> and the graph's <c>Shared</c> helpers via a metatable <c>__index</c> → the VM
/// globals. Each tick: fire a pending requested transition; else run the current state's
/// <c>tick()</c>, then evaluate every outgoing transition's <c>check()</c> in order — the first
/// truthy one fires (<c>from.on_exit()</c> → <c>to.on_enter()</c>). Events route to the current
/// state's hook. A transition can also be requested explicitly (<c>RequestState</c> / the
/// operator API / <c>bot.requestState</c>).</para></summary>
public sealed class BehaviorGraphRunner : IDisposable
{
    private static readonly object RegisterGate = new();
    private static bool _typesRegistered;

    private readonly BotHandle _handle;
    private readonly BotApi _api;
    private readonly BehaviorGraph _graph;
    private readonly Action<string> _log;
    private readonly int _tickMs;
    private readonly Action<string>? _persistState;
    private readonly BlockingCollection<BotEvent> _events = new(new ConcurrentQueue<BotEvent>());
    private readonly CancellationTokenSource _cts;
    private readonly Thread _thread;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private Script? _lua;
    private sealed class Node { public DynValue OnEnter = DynValue.Nil, Tick = DynValue.Nil, OnExit = DynValue.Nil; public Dictionary<string, DynValue> Hooks = new(); }
    private sealed class Trans { public string Name = "", From = "", To = ""; public DynValue Check = DynValue.Nil; }
    private readonly Dictionary<string, Node> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Trans> _transitions = new();

    private string _current = "";
    private volatile string? _requested;
    private volatile bool _unpin;
    private bool _pinned; // while pinned (after an operator flip), autonomous transitions are suppressed
    private long _ticks;
    private volatile string _runState = "starting";
    private volatile string? _lastError;
    private int _disposed;

    internal BehaviorGraphRunner(BotHandle handle, BotApi api, BehaviorGraph graph, Action<string> log,
        CancellationToken botCt, int tickMs = 250, string? startState = null, Action<string>? persistState = null)
    {
        _handle = handle;
        _api = api;
        _graph = graph;
        _log = log;
        _tickMs = Math.Clamp(tickMs, 20, 60_000);
        _current = startState ?? graph.Initial;
        _persistState = persistState;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(botCt);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = $"graph-{handle.Id}" };
    }

    public string GraphName => _graph.Name;
    public string CurrentState => _current;
    public string StatusLine => $"{_graph.Name}:{_current} [{_runState}] ticks={Interlocked.Read(ref _ticks)}";

    /// <summary>Request a transition to <paramref name="state"/> on the next tick (operator
    /// flip / <c>bot.requestState</c>). Fires even with no defined edge (a forced switch) and
    /// <b>pins</b> the graph there — autonomous transitions are suppressed until you request
    /// the special state <c>"auto"</c>, which resumes autonomous behaviour from the current
    /// state. (So a deliberate flip to e.g. stay_alive holds for "90% control".)</summary>
    public void RequestState(string state)
    {
        if (string.Equals(state, "auto", StringComparison.OrdinalIgnoreCase)) _unpin = true;
        else _requested = state;
    }

    internal void Start()
    {
        _handle.Events += OnEvent;
        _api.RequestStateHandler = RequestState; // let Lua call bot.requestState(name)
        _thread.Start();
    }

    private void OnEvent(BotEvent e)
    {
        if (_cts.IsCancellationRequested || _events.IsAddingCompleted) return;
        if (_events.Count > 2000) return;
        try { _events.Add(e); } catch { /* completed/disposed */ }
    }

    private void RunLoop()
    {
        var ct = _cts.Token;
        try
        {
            Setup();
            _runState = "running";
            Enter(_current);

            var nextTick = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                var wait = (int)Math.Clamp(nextTick - Environment.TickCount64, 0, _tickMs);
                if (_events.TryTake(out var ev, wait, ct)) { Dispatch(ev); continue; }
                if (Environment.TickCount64 >= nextTick)
                {
                    Interlocked.Increment(ref _ticks);
                    TickOnce();
                    nextTick = Environment.TickCount64 + _tickMs;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _lastError = ex.Message; _log($"[graph:{_graph.Name}] fatal: {ex.Message}"); }
        finally { _runState = "stopped"; }
    }

    private void TickOnce()
    {
        // 0) Resume autonomous behaviour if the operator released the pin ("auto").
        if (_unpin) { _unpin = false; if (_pinned) { _pinned = false; _log($"[graph:{_graph.Name}] autonomous transitions resumed (unpinned at {_current})"); } }

        // 1) An explicit request wins and PINS the state (operator flip / bot.requestState).
        if (_requested is { } req) { _requested = null; FireTo(req, "request"); _pinned = true; }

        // 2) Run the current state.
        if (_states.TryGetValue(_current, out var cur)) SafeCall(cur.Tick, "tick");

        // 3) Evaluate outgoing transitions in order; first truthy check fires — unless pinned.
        if (_pinned) return;
        foreach (var t in _transitions)
        {
            if (!string.Equals(t.From, _current, StringComparison.OrdinalIgnoreCase)) continue;
            if (Truthy(SafeEval(t.Check, $"transition {t.Name}.check"))) { FireTo(t.To, t.Name); break; }
        }
    }

    private void Enter(string state)
    {
        _current = state;
        _persistState?.Invoke(state);
        if (_states.TryGetValue(state, out var n)) { _log($"[graph:{_graph.Name}] -> {state}"); SafeCall(n.OnEnter, "on_enter"); }
        else _log($"[graph:{_graph.Name}] WARN entered unknown state '{state}'");
    }

    private void FireTo(string to, string via)
    {
        if (string.Equals(to, _current, StringComparison.OrdinalIgnoreCase)) return;
        if (!_states.ContainsKey(to)) { _log($"[graph:{_graph.Name}] WARN transition to unknown state '{to}' (via {via})"); return; }
        if (_states.TryGetValue(_current, out var cur)) SafeCall(cur.OnExit, "on_exit");
        _log($"[graph:{_graph.Name}] {_current} -> {to} (via {via})");
        Enter(to);
    }

    private void Dispatch(BotEvent ev)
    {
        if (!_states.TryGetValue(_current, out var n)) return;
        switch (ev.Kind)
        {
            case BotEventKind.Chat when ev.Data is ChatMessage m: CallHook(n, "on_chat", ChatTable(m)); break;
            case BotEventKind.CastFail when ev.Data is ushort r:
                CallHook(n, "on_cast_fail", DynValue.NewNumber(r), DynValue.NewString(ZoneView.CastFailReason.Describe(r))); break;
            case BotEventKind.Hp when ev.Data is uint hp:
                CallHook(n, "on_hp", DynValue.NewNumber(hp), DynValue.NewNumber(_handle.ZoneView?.MaxHp ?? 0)); break;
            case BotEventKind.Sp when ev.Data is uint sp:
                CallHook(n, "on_sp", DynValue.NewNumber(sp), DynValue.NewNumber(_handle.ZoneView?.MaxSp ?? 0)); break;
            case BotEventKind.Hit when ev.Data is HitInfo hit: CallHook(n, "on_hit", HitTable(hit)); break;
            case BotEventKind.MapChanged: CallHook(n, "on_map", DynValue.NewString(_handle.CurrentMap ?? "")); break;
            case BotEventKind.MoveFailed when ev.Data is ValueTuple<uint, uint> pos:
                CallHook(n, "on_move_fail", DynValue.NewNumber(pos.Item1), DynValue.NewNumber(pos.Item2)); break;
        }
    }

    private void CallHook(Node n, string hook, params DynValue[] args)
    {
        if (n.Hooks.TryGetValue(hook, out var fn)) SafeCall(fn, hook, args);
    }

    // ── VM setup: shared globals + per-node isolated environments ─────────────────────────
    private void Setup()
    {
        _lua = new Script(CoreModules.Preset_SoftSandbox);
        lock (RegisterGate) { if (!_typesRegistered) { UserData.RegisterType<BotApi>(); _typesRegistered = true; } }
        _api.AttachScript(_lua);
        _lua.Globals["bot"] = _api;
        _lua.Globals["log"] = (Action<string>)(m => _log($"[graph:{_graph.Name}:{_current}] {m}"));
        _lua.Options.DebugPrint = m => _log($"[graph:{_graph.Name}:{_current}] {m}");

        // Shared helpers go into the VM globals so every node/transition env sees them
        // (via metatable __index), alongside bot/log. This is the "import a shared helper"
        // composition: e.g. survive() defined here, called by both stay_alive and mob_grind.
        if (!string.IsNullOrWhiteSpace(_graph.Shared))
            try { _lua.DoString(_graph.Shared, _lua.Globals, "shared"); }
            catch (Exception ex) { _log($"[graph:{_graph.Name}] shared script error: {ex.Message}"); }

        foreach (var s in _graph.States)
        {
            var env = NewEnv();
            try { _lua.DoString(s.Script, env, s.Name); }
            catch (Exception ex) { _log($"[graph:{_graph.Name}] state '{s.Name}' load error: {ex.Message}"); continue; }
            var node = new Node { OnEnter = env.Get("on_enter"), Tick = env.Get("tick"), OnExit = env.Get("on_exit") };
            foreach (var hook in new[] { "on_chat", "on_cast_fail", "on_hp", "on_sp", "on_hit", "on_map", "on_move_fail" })
            {
                var fn = env.Get(hook);
                if (fn.Type == DataType.Function) node.Hooks[hook] = fn;
            }
            _states[s.Name] = node;
        }

        foreach (var t in _graph.Transitions)
        {
            var env = NewEnv();
            try { _lua.DoString(t.Check, env, $"{t.Name}.check"); }
            catch (Exception ex) { _log($"[graph:{_graph.Name}] transition '{t.Name}' load error: {ex.Message}"); continue; }
            _transitions.Add(new Trans { Name = t.Name, From = t.From, To = t.To, Check = env.Get("check") });
        }
    }

    /// <summary>A fresh environment table that resolves unknown globals (bot/log/shared
    /// helpers) from the VM globals via __index — so each chunk's own functions/locals stay
    /// isolated while shared API is visible.</summary>
    private Table NewEnv()
    {
        var env = new Table(_lua);
        var meta = new Table(_lua);
        meta["__index"] = _lua!.Globals;
        env.MetaTable = meta;
        return env;
    }

    private void SafeCall(DynValue fn, string what, params DynValue[] args)
    {
        if (_lua is null || fn.Type != DataType.Function) return;
        try { _lua.Call(fn, args); }
        catch (ScriptRuntimeException ex) { _lastError = ex.DecoratedMessage; _log($"[graph:{_graph.Name}:{_current}] {what} error: {ex.DecoratedMessage}"); }
        catch (Exception ex) { _lastError = ex.Message; _log($"[graph:{_graph.Name}:{_current}] {what} error: {ex.Message}"); }
    }

    private DynValue SafeEval(DynValue fn, string what)
    {
        if (_lua is null || fn.Type != DataType.Function) return DynValue.Nil;
        try { return _lua.Call(fn); }
        catch (ScriptRuntimeException ex) { _lastError = ex.DecoratedMessage; _log($"[graph:{_graph.Name}:{_current}] {what} error: {ex.DecoratedMessage}"); return DynValue.Nil; }
        catch (Exception ex) { _lastError = ex.Message; _log($"[graph:{_graph.Name}:{_current}] {what} error: {ex.Message}"); return DynValue.Nil; }
    }

    private static bool Truthy(DynValue v) => v.Type != DataType.Nil && v.Type != DataType.Void && !(v.Type == DataType.Boolean && !v.Boolean);

    private DynValue ChatTable(ChatMessage m) { var t = new Table(_lua); t["handle"] = m.Handle; t["name"] = m.SenderName; t["text"] = m.Text; return DynValue.NewTable(t); }
    private DynValue HitTable(HitInfo h) { var t = new Table(_lua); t["attacker"] = h.Attacker; t["defender"] = h.Defender; t["damage"] = h.Damage; t["restHp"] = h.RestHp; return DynValue.NewTable(t); }

    public ScriptStatus Status() => new(_graph.Name, _runState, Interlocked.Read(ref _ticks), 0, _lastError,
        Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1), _states.Keys.ToArray(), _current);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _handle.Events -= OnEvent;
        try { _cts.Cancel(); } catch { }
        if (_thread.IsAlive && Thread.CurrentThread != _thread) { try { _thread.Join(1000); } catch { } }
        _events.Dispose();
        _cts.Dispose();
    }
}
