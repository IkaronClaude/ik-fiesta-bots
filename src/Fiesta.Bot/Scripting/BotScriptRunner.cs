using System.Collections.Concurrent;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Navigation;
using Fiesta.Bot.Session;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>Debug view of a running script, returned by the status endpoint.</summary>
public sealed record ScriptStatus(
    string Name, string State, long Ticks, long EventsHandled, string? LastError,
    double UptimeSeconds, IReadOnlyList<string> Globals);

/// <summary>
/// Runs ONE Lua behaviour script for ONE bot on a dedicated thread. The thread owns
/// the MoonSharp VM (which isn't thread-safe), so ALL Lua runs on it: a
/// <see cref="BlockingCollection{T}"/> marshals events off the session read loop, and
/// a <c>tick</c> fires on an interval between events. This is the single-threaded
/// message-pump pattern — no locking around the VM, no re-entrancy.
///
/// <para>The script defines any subset of <c>on_start / tick / on_chat / on_hit /
/// on_cast_fail / on_hp / on_sp / on_player / on_player_left / on_map / on_move_fail /
/// on_stop</c>; the runner calls the ones present. Injected globals: <c>bot</c> (a
/// <see cref="BotApi"/>) and <c>log</c>. A callback that throws is logged and the loop
/// continues — a bad script never kills the bot.</para>
///
/// <para>Subscribes to <see cref="BotHandle.Events"/> (the stable hub) rather than the
/// live <see cref="ZoneView"/>, so the script keeps reacting across a cross-server
/// ZoneView swap.</para>
/// </summary>
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
    private readonly BlockingCollection<BotEvent> _events = new(new ConcurrentQueue<BotEvent>());
    private readonly CancellationTokenSource _cts;
    private readonly Thread _thread;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private Script? _lua;
    private long _ticks;
    private long _eventsHandled;
    private volatile string _state = "starting";
    private volatile string? _lastError;
    private int _disposed;

    internal BotScriptRunner(BotHandle handle, BotApi api, string name, string source,
        Action<string> log, CancellationToken botCt, int tickMs = 250)
    {
        _handle = handle;
        _api = api;
        _name = name;
        _source = source;
        _log = log;
        _tickMs = Math.Clamp(tickMs, 20, 60_000);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(botCt);
        _thread = new Thread(RunLoop) { IsBackground = true, Name = $"lua-{handle.Id}" };
    }

    public string Name => _name;

    /// <summary>One-line status for the bot snapshot (<c>name [state] ticks=N</c>).</summary>
    public string StatusLine => $"{_name} [{_state}] ticks={Interlocked.Read(ref _ticks)}";

    /// <summary>Start the script thread and begin receiving events. Idempotent-safe.</summary>
    internal void Start()
    {
        _handle.Events += OnEvent;
        _thread.Start();
    }

    public ScriptStatus Status()
    {
        IReadOnlyList<string> globals;
        // Reading globals from another thread touches the VM, which isn't thread-safe —
        // but we only enumerate the keys (a snapshot of names), tolerating a race, and
        // never call into Lua. Good enough for a debug surface.
        try { globals = _lua?.Globals.Keys.Select(k => k.ToPrintString()).Where(s => s.Length > 0).Take(64).ToArray() ?? []; }
        catch { globals = []; }
        return new ScriptStatus(
            _name, _state, Interlocked.Read(ref _ticks), Interlocked.Read(ref _eventsHandled),
            _lastError, Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1), globals);
    }

    private void OnEvent(BotEvent e)
    {
        // Runs on the session read loop — must not block. Enqueue and return; drop if
        // the script falls badly behind so a slow script can't grow memory unbounded.
        if (_cts.IsCancellationRequested || _events.IsAddingCompleted) return;
        if (_events.Count > 2000) return;
        try { _events.Add(e); } catch { /* completed/disposed mid-add */ }
    }

    private void RunLoop()
    {
        var ct = _cts.Token;
        try
        {
            Setup();
            SafeCall("on_start");
            _state = "running";

            var nextTick = Environment.TickCount64;
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
                    Interlocked.Increment(ref _ticks);
                    SafeCall("tick");
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
        // SoftSandbox = string/table/math/os-time but NO file io / os.execute / require.
        _lua = new Script(CoreModules.Preset_SoftSandbox);
        lock (RegisterGate)
        {
            if (!_typesRegistered) { UserData.RegisterType<BotApi>(); _typesRegistered = true; }
        }
        _api.AttachScript(_lua);
        _lua.Globals["bot"] = _api;
        _lua.Globals["log"] = (Action<string>)(m => _log($"[script:{_name}] {m}"));
        _lua.DoString(_source, codeFriendlyName: _name);
    }

    private void Dispatch(BotEvent ev)
    {
        Interlocked.Increment(ref _eventsHandled);
        switch (ev.Kind)
        {
            case BotEventKind.Chat when ev.Data is ChatMessage m:
                SafeCall("on_chat", ChatTable(m)); break;
            case BotEventKind.CastFail when ev.Data is ushort r:
                SafeCall("on_cast_fail", DynValue.NewNumber(r)); break;
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

    /// <summary>Call a Lua global if it's defined as a function. A script error is
    /// recorded + logged but does not stop the loop (the next tick/event still fires).</summary>
    private void SafeCall(string fn, params DynValue[] args)
    {
        if (_lua is null) return;
        var f = _lua.Globals.Get(fn);
        if (f.Type != DataType.Function) return;
        try { _lua.Call(f, args); }
        catch (ScriptRuntimeException ex) { _lastError = ex.DecoratedMessage; _log($"[script:{_name}] {fn} error: {ex.DecoratedMessage}"); }
        catch (Exception ex) { _lastError = ex.Message; _log($"[script:{_name}] {fn} error: {ex.Message}"); }
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
        // Don't block the caller (an HTTP thread) for long: the loop is cancellable and
        // its only blocking point is the event take, which the token aborts immediately.
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _cts.Dispose();
        _events.Dispose();
    }
}
