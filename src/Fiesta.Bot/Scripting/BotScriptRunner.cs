using System.Collections.Concurrent;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Navigation;
using Fiesta.Bot.Session;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>Debug view of a running script, returned by the status endpoint</summary>
public sealed record ScriptStatus(
    string Name, string State, long Ticks, long EventsHandled, string? LastError,
    double UptimeSeconds, IReadOnlyList<string> Globals, string? SmState);

/// <summary>Runs ONE Lua behaviour script for ONE bot on a dedicated thread</summary>
public sealed class BotScriptRunner : IDisposable
{
    private static readonly object RegisterGate = new();
    private static bool _typesRegistered;

    private readonly BotHandle _handle;
    private readonly BotApi _api;
    private readonly string _name;
    private readonly string _source;
    private readonly Action<string> _log;
    private readonly int _tickMs;
    private readonly bool _trace;
    private readonly BlockingCollection<BotEvent> _events = new(new ConcurrentQueue<BotEvent>());
    private readonly CancellationTokenSource _cts;
    private readonly Thread _thread;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private Script? _lua;
    private long _ticks;
    private long _eventsHandled;
    private volatile string _state = "starting";
    private volatile string? _lastError;
    private volatile string? _smState; // current state-machine state (null for a plain script)
    private int _disposed;

    internal BotScriptRunner(BotHandle handle, BotApi api, string name, string source,
        Action<string> log, CancellationToken botCt, int tickMs = 250, bool trace = false)
    {
        _handle = handle;
        _api = api;
        _name = name;
        _source = source;
        _log = log;
        _tickMs = Math.Clamp(tickMs, 20, 60_000);
        _trace = trace;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(botCt);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = $"lua-{handle.Id}" };
    }

    public string Name => _name;

    /// <summary>One-line status for the bot snapshot ( name [state] ticks=N )</summary>
    public string StatusLine => $"{_name} [{_state}] ticks={Interlocked.Read(ref _ticks)}";

    /// <summary>Start the script thread and begin receiving events</summary>
    internal void Start()
    {
        _handle.Events += OnEvent;
        _thread.Start();
    }

    // Published BY the script thread, read by everyone else
    private IReadOnlyList<string>? _globalsSnapshot;
    private void RefreshGlobalsSnapshot()
    {
        try { _globalsSnapshot = _lua?.Globals.Keys.Select(k => k.ToPrintString()).Where(x => x.Length > 0).Take(64).ToArray() ?? []; }
        catch { /* best-effort debug surface */ }
    }

    public ScriptStatus Status()
    {
        // THIS USED TO ENUMERATE _lua.Globals.Keys FROM THE CALLER'S THREAD
        var globals = Volatile.Read(ref _globalsSnapshot) ?? [];
        return new ScriptStatus(
            _name, _state, Interlocked.Read(ref _ticks), Interlocked.Read(ref _eventsHandled),
            _lastError, Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1), globals, _smState);
    }

    private void OnEvent(BotEvent e)
    {
        // Runs on the session read loop — must not block
        if (_cts.IsCancellationRequested || _events.IsAddingCompleted) return;
        if (_events.Count > 2000) return;
        try { _events.Add(e); } catch { /* completed/disposed mid-add */ }
    }

    private int _suspended;              // 0 = running, 1 = suspended (zone transition in flight)
    private int _ticksSuspended;         // how many ticks we skipped while suspended
    private long _suspendStartedAt;      // TickCount64 when the current suspension began

    /// <summary>How long a zone-transition suspend may last before we treat it as a dead session and stop</summary>
    private const long SuspendMaxMs = 120_000;
    private string _suspendReason = "";

    /// <summary>Stop ticking the script WITHOUT tearing it down, for the duration of a zone transition</summary>
    public void Suspend(string reason)
    {
        _suspendReason = reason;
        if (Interlocked.Exchange(ref _suspended, 1) == 0)
        {
            Interlocked.Exchange(ref _ticksSuspended, 0);
            _log($"[script] SUSPEND — {reason} (leveler will stop sending until the new zone is live)");
        }
    }

    /// <summary>Resume ticking after a transition completes</summary>
    public void Resume(string reason)
    {
        if (Interlocked.Exchange(ref _suspended, 0) == 1)
            _log($"[script] RESUME — {reason} (skipped {Volatile.Read(ref _ticksSuspended)} tick(s) while suspended)");
    }

    private void RunLoop()
    {
        // THIS WAS OUTSIDE THE try AND IT KILLED THE WHOLE HOST
        CancellationToken ct;
        try { ct = _cts.Token; }
        catch (ObjectDisposedException)
        {
            _state = "stopped";
            _handle.Events -= OnEvent;
            return;
        }
        try
        {
            Setup();
            SafeCall("on_start");
            RefreshGlobalsSnapshot();
            _state = "running";

            var nextTick = Environment.TickCount64;
            _suspendStartedAt = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                var wait = (int)Math.Clamp(nextTick - Environment.TickCount64, 0, _tickMs);
                if (_events.TryTake(out var ev, wait, ct))
                {
                    Dispatch(ev);
                    while (_events.TryTake(out var more, 0)) Dispatch(more); // drain the burst
                }
                if (Environment.TickCount64 >= nextTick)
                {
                    // SUSPENDED = a zone transition is in flight
                    if (Volatile.Read(ref _suspended) != 0)
                    {
                        var n = Interlocked.Increment(ref _ticksSuspended);
                        if (n == 1) _suspendStartedAt = Environment.TickCount64;
                        if (n == 1 || n % 10 == 0)
                            _log($"[script] tick SUSPENDED ({n} skipped) — {_suspendReason}");
                        if (Environment.TickCount64 - _suspendStartedAt > SuspendMaxMs)
                        {
                            _log($"[script:{_name}] CRUTCH[CRIT] SUSPENDED for {(Environment.TickCount64 - _suspendStartedAt) / 1000}s " +
                                 $"({n} ticks) waiting on: {_suspendReason} — that never resumed. STOPPING the script; " +
                                 "the bot needs a respawn (a suspend with no exit silently hides a dead session).");
                            _state = "stopped";
                            break;
                        }
                        nextTick = Environment.TickCount64 + _tickMs;
                        continue;
                    }
                    Interlocked.Increment(ref _ticks);
                    SafeCall("tick");
                    // Cheap, and on the ONLY thread allowed to touch the VM
                    if (Interlocked.Read(ref _ticks) % 25 == 0) RefreshGlobalsSnapshot();
                    nextTick = Environment.TickCount64 + _tickMs;
                }
            }
            _state = "stopped";
        }
        catch (OperationCanceledException) { _state = "stopped"; }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _state = "error";
            _log($"[script:{_name}] FATAL: {ex.Message}");
        }
        finally
        {
            try { SafeCall("on_stop"); } catch { /* best-effort */ }
            _handle.Events -= OnEvent;
        }
    }

    private void Setup()
    {
        // SoftSandbox = string/table/math/os-time but NO file io / os.execute / require
        _lua = new Script(CoreModules.Preset_SoftSandbox);
        lock (RegisterGate)
        {
            if (!_typesRegistered) { UserData.RegisterType<BotApi>(); _typesRegistered = true; }
        }
        _api.AttachScript(_lua);
        _api.StateReporter = s => _smState = s; // state-machine current state -> status
        _lua.Globals["bot"] = _api;
        // Both an explicit log() and Lua's built-in print(...) reach the bot log (and thus the console + the live log-st…
        _lua.Globals["log"] = (Action<string>)(m => _log($"[script:{_name}] {m}"));
        // Verbosity-leveled siblings: logi(...) = progress (kills/quest credit), logv(...) = per-tick firehose (move/cas…
        _lua.Globals["logi"] = (Action<string>)(m => _handle.Log(BotLogLevel.Info, $"[script:{_name}] {m}"));
        _lua.Globals["logv"] = (Action<string>)(m => _handle.Log(BotLogLevel.Verbose, $"[script:{_name}] {m}"));
        _lua.Options.DebugPrint = m => _log($"[script:{_name}] {m}");
        // Layer 2: the state-machine harness
        _lua.DoString(StateMachineHarness, codeFriendlyName: "sm-harness");
        // Trace mode: wrap `bot` in a proxy that logs every call before forwarding, so the log stream shows each bot.* i…
        if (_trace) _lua.DoString(TraceShim, codeFriendlyName: "trace-shim");
        _lua.DoString(_source, codeFriendlyName: _name);
    }

    // A behaviour tree / state machine, in pure Lua on the existing runtime
    private const string StateMachineHarness = @"
function statemachine(states, initial)
  assert(type(states) == 'table', 'statemachine: states must be a table')
  assert(states[initial] ~= nil, 'statemachine: no initial state ' .. tostring(initial))
  local current = nil

  local function enter(name)
    current = name
    if bot and bot.__state then bot.__state(name) end
    log('[sm] -> ' .. name)
    local st = states[name]
    if st.on_enter then st.on_enter() end
  end

  local function switch(to)
    if to == nil or to == current then return end
    if states[to] == nil then log('[sm] WARN unknown state ' .. tostring(to)); return end
    local st = states[current]
    if st and st.on_exit then st.on_exit() end
    enter(to)
  end

  local function dispatch(ev, ...)
    local st = states[current]
    if st and st[ev] then st[ev](...) end
  end

  function on_start() enter(initial) end
  function tick()
    local st = states[current]
    if st == nil then return end
    if st.tick then st.tick() end
    if st.next then switch(st.next()) end
  end
  function on_chat(m) dispatch('on_chat', m) end
  function on_hit(e) dispatch('on_hit', e) end
  function on_cast_fail(r, reason) dispatch('on_cast_fail', r, reason) end
  function on_hp(h, m) dispatch('on_hp', h, m) end
  function on_sp(s, m) dispatch('on_sp', s, m) end
  function on_player(p) dispatch('on_player', p) end
  function on_player_left(h) dispatch('on_player_left', h) end
  function on_map(m) dispatch('on_map', m) end
  function on_move_fail(x, y) dispatch('on_move_fail', x, y) end
  function on_stop()
    local st = states[current]
    if st and st.on_exit then st.on_exit() end
    dispatch('on_stop')
  end
end
";

    // Replaces `bot` with a metatable proxy whose __index returns, for each function member, a closure that logs `call bot
    private const string TraceShim = @"
do
  local real = bot
  local function argstr(...)
    local n = select('#', ...)
    local t = {}
    for i = 1, n do t[i] = tostring((select(i, ...))) end
    return table.concat(t, ', ')
  end
  bot = setmetatable({}, { __index = function(_, k)
    local v = real[k]
    if type(v) == 'function' then
      return function(...) log('call bot.' .. k .. '(' .. argstr(...) .. ')'); return v(...) end
    end
    return v
  end })
end
";

    private void Dispatch(BotEvent ev)
    {
        Interlocked.Increment(ref _eventsHandled);
        switch (ev.Kind)
        {
            case BotEventKind.Chat when ev.Data is ChatMessage m:
                SafeCall("on_chat", ChatTable(m)); break;
            case BotEventKind.CastFail when ev.Data is ushort r:
                var reason = Session.ZoneView.CastFailReason.Describe(r);
                _log($"[script:{_name}] cast failed: {reason} (0x{r:X4})");
                SafeCall("on_cast_fail", DynValue.NewNumber(r), DynValue.NewString(reason)); break;
            case BotEventKind.PlayerAppeared when ev.Data is NearbyPlayer p:
                SafeCall("on_player", PlayerTable(p)); break;
            case BotEventKind.PlayerLeft when ev.Data is ushort h:
                SafeCall("on_player_left", DynValue.NewNumber(h)); break;
            case BotEventKind.MapChanged when ev.Data is MapHandoff:
                SafeCall("on_map", DynValue.NewString(_handle.CurrentMap ?? "")); break;
            case BotEventKind.MoveFailed when ev.Data is ValueTuple<uint, uint> pos:
                SafeCall("on_move_fail", DynValue.NewNumber(pos.Item1), DynValue.NewNumber(pos.Item2)); break;
            case BotEventKind.Hp when ev.Data is uint hp:
                SafeCall("on_hp", DynValue.NewNumber(hp), DynValue.NewNumber(_handle.ZoneView?.MaxHp ?? 0)); break;
            case BotEventKind.Sp when ev.Data is uint sp:
                SafeCall("on_sp", DynValue.NewNumber(sp), DynValue.NewNumber(_handle.ZoneView?.MaxSp ?? 0)); break;
            case BotEventKind.Hit when ev.Data is HitInfo hit:
                SafeCall("on_hit", HitTable(hit)); break;
        }
    }

    /// <summary>Call a Lua global if it's defined as a function</summary>
    private void SafeCall(string fn, params DynValue[] args)
    {
        if (_lua is null) return;
        var f = _lua.Globals.Get(fn);
        if (f.Type != DataType.Function) return;
        try { _lua.Call(f, args); }
        catch (ScriptRuntimeException ex) { _lastError = ex.DecoratedMessage; _log($"[script:{_name}] {fn} error: {ex.DecoratedMessage}"); }
        // A .NET exception thrown INSIDE a bot.* callback is NOT a ScriptRuntimeException, so it lands here — and this u…
        catch (Exception ex)
        {
            _lastError = ex.Message;
            var frames = (ex.StackTrace ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("at ", StringComparison.Ordinal))
                .Take(4)
                // Drop the noisy generic/async plumbing so the useful frame is visible at a glance
                .Select(l => l.Replace("Fiesta.Bot.Scripting.", "").Replace("Fiesta.Bot.", ""));
            var where = string.Join(" <- ", frames);
            _log($"[script:{_name}] {fn} error: {ex.GetType().Name}: {ex.Message}" +
                 (where.Length > 0 ? $"  |  {where}" : "  |  (no stack)"));
        }
    }

    private DynValue ChatTable(ChatMessage m)
    {
        var t = new Table(_lua);
        t["handle"] = m.Handle; t["name"] = m.SenderName; t["text"] = m.Text;
        return DynValue.NewTable(t);
    }

    private DynValue PlayerTable(NearbyPlayer p)
    {
        var t = new Table(_lua);
        t["handle"] = p.Handle; t["name"] = p.Name; t["class"] = p.Class;
        t["level"] = p.Level; t["x"] = p.X; t["y"] = p.Y;
        return DynValue.NewTable(t);
    }

    private DynValue HitTable(HitInfo h)
    {
        var t = new Table(_lua);
        t["attacker"] = h.Attacker; t["defender"] = h.Defender;
        t["damage"] = h.Damage; t["restHp"] = h.RestHp;
        t["self"] = _handle.SelfHandle is { } s && (h.Attacker == s || h.Defender == s);
        return DynValue.NewTable(t);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _handle.Events -= OnEvent;
        try { _cts.Cancel(); } catch { }
        _events.CompleteAdding();
        // Don't block the caller (an HTTP thread) for long: the loop is cancellable and its only blocking point is the e…
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _cts.Dispose();
        _events.Dispose();
    }
}
