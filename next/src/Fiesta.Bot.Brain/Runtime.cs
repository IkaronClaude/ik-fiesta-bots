using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Brain;

/// <summary>Blackboard scopes (Lua tables bb.bot / bb.layer / bb.state).</summary>
public enum BlackboardScope : byte { Bot, Layer, State }

/// <summary>One recorded transition (API_SPEC 7.1 + UI U12).</summary>
public sealed record TransitionRecord(long Tick, DateTime At, string Layer, string From, string To, string Kind,
    string Reason, IReadOnlyDictionary<string, double> Values, int Depth, int Index, string LeafFrom);

/// <summary>Evaluation of one outgoing transition in the last evaluation (debugger condition panel, UI U11).</summary>
public sealed record ConditionTrace(string To, string Kind, string Expr, double? Value, bool? Result, int HoldMs,
    string State, int Depth, int Index, int HoldTarget);

/// <summary>Live state of one layer for the debugger (UI U10).</summary>
public sealed record LayerTrace(string Name, int Priority, IReadOnlyList<string> Path, DateTime? EnteredAt, long TimeInStateMs,
    IReadOnlyList<DateTime> EnteredAtPath, double? Progress, DateTime? ProgressBestAt, int BudgetMs, IReadOnlyList<string> Claims,
    IReadOnlyList<ConditionTrace> Conditions);

/// <summary>Action row for the debugger.</summary>
public sealed record ActionTrace(int Id, string Kind, string State, int Code, string Reason, DateTime SentAt, DateTime? ResolvedAt, string Layer);

/// <summary>Procedure row for the debugger.</summary>
public sealed record ProcedureTrace(string Kind, string Step, DateTime Since);

/// <summary>Tick summary for the debugger.</summary>
public sealed record TickSummary(double P50Us, double P99Us, double AllocP95B, IReadOnlyDictionary<string, double> Steps);

/// <summary>A recorded Lua or runtime error.</summary>
public sealed record ErrorTrace(long Tick, DateTime At, string Layer, string State, string Error);

/// <summary>Everything the debugger shows for one bot at one moment (API_SPEC 7.1 with UI U8, U9, U13).</summary>
public sealed record DebugFrame(string Bot, long Tick, DateTime T, string Version, bool Paused, IReadOnlyList<LayerTrace> Layers,
    IReadOnlyList<TransitionRecord> LastTransitions, JsonObject Blackboard, IReadOnlyDictionary<string, double> Watches,
    IReadOnlyList<ActionTrace> Actions, IReadOnlyList<ProcedureTrace> Procedures, TickSummary TickStats, IReadOnlyList<ErrorTrace> Errors);

/// <summary>Zero-argument numeric readings usable in `value:` conditions and `progress:` (self.hpPct, threat.onUs...),
/// bound by reflection once per bot to the module instances (no Lua call, no allocation per read).</summary>
public sealed class SensorRegistry
{
    private readonly Dictionary<string, Func<double>> _s = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Register a sensor.</summary>
    public void Register(string path, Func<double> read) => _s[path] = read;
    /// <summary>Read a sensor (NaN unknown).</summary>
    public double Read(string path) => _s.TryGetValue(path, out var f) ? f() : double.NaN;
    /// <summary>Lookup.</summary>
    public Func<double>? Get(string path) => _s.GetValueOrDefault(path);
    /// <summary>Known sensor paths.</summary>
    public IReadOnlyCollection<string> Paths => _s.Keys;

    /// <summary>The default sensors: every zero-parameter API function returning a number or boolean.</summary>
    public static SensorRegistry Create(ApiModule[] modules)
    {
        var r = new SensorRegistry();
        for (var i = 0; i < modules.Length; i++)
        {
            var m = modules[i];
            var name = GeneratedApi.ModuleNames[i];
            foreach (var mi in m.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (mi.GetCustomAttribute<LuaFnAttribute>() is not { } a || mi.GetParameters().Length != 0) continue;
                var lua = a.Name ?? char.ToLowerInvariant(mi.Name[0]) + mi.Name[1..];
                var t = mi.ReturnType;
                Func<double>? f = null;
                if (t == typeof(double)) f = (Func<double>)mi.CreateDelegate(typeof(Func<double>), m);
                else if (t == typeof(int)) { var g = (Func<int>)mi.CreateDelegate(typeof(Func<int>), m); f = () => g(); }
                else if (t == typeof(long)) { var g = (Func<long>)mi.CreateDelegate(typeof(Func<long>), m); f = () => g(); }
                else if (t == typeof(bool)) { var g = (Func<bool>)mi.CreateDelegate(typeof(Func<bool>), m); f = () => g() ? 1 : 0; }
                if (f is not null) r.Register(name + "." + lua, f);
            }
        }
        return r;
    }

    /// <summary>The sensor paths a fresh registry has (for the linter / manifest without a bot).</summary>
    public static IReadOnlySet<string> StaticPaths()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ApiManifest.ZeroArgNumeric()) s.Add(p);
        return s;
    }
}

/// <summary>Collects transitions, condition traces, watches and errors; builds DebugFrames on request.</summary>
public sealed class DebugTap : IDbgSink
{
    private readonly Queue<TransitionRecord> _transitions = new();
    private readonly Queue<ErrorTrace> _errors = new();
    private readonly Dictionary<string, double> _watches = new();
    private int _linesThisTick;
    /// <summary>Whether anyone is watching (detailed condition traces are recorded only then).</summary>
    public bool Watched { get; set; }
    /// <summary>Raised on each transition (any thread may subscribe; must not block).</summary>
    public event Action<TransitionRecord>? TransitionRecorded;
    /// <summary>Transitions (lifetime count).</summary>
    public long TransitionCount { get; private set; }
    /// <summary>Errors (lifetime count).</summary>
    public long ErrorCount { get; private set; }
    /// <summary>The last transition, null none.</summary>
    public TransitionRecord? Last { get; private set; }

    /// <summary>Record a transition.</summary>
    public void Transition(TransitionRecord record)
    {
        lock (_transitions) { _transitions.Enqueue(record); if (_transitions.Count > 500) _transitions.Dequeue(); }
        TransitionCount++; Last = record;
        TransitionRecorded?.Invoke(record);
    }
    /// <summary>Recent transitions, newest last.</summary>
    public IReadOnlyList<TransitionRecord> Transitions() { lock (_transitions) return _transitions.ToArray(); }
    /// <summary>Record an error.</summary>
    public void Error(ErrorTrace error) { lock (_errors) { _errors.Enqueue(error); if (_errors.Count > 50) _errors.Dequeue(); } ErrorCount++; }
    /// <summary>Recent errors, newest last.</summary>
    public IReadOnlyList<ErrorTrace> Errors() { lock (_errors) return _errors.ToArray(); }
    /// <inheritdoc/>
    public void Watch(string name, double value) { lock (_watches) _watches[name] = value; }
    /// <summary>Watches.</summary>
    public IReadOnlyDictionary<string, double> Watches() { lock (_watches) return new Dictionary<string, double>(_watches); }
    /// <inheritdoc/>
    public void Mark(string section) { }
    /// <inheritdoc/>
    public bool TakeLine() => ++_linesThisTick <= 5;
    /// <summary>New tick (line budget).</summary>
    public void BeginTick() => _linesThisTick = 0;
}

/// <summary>A compiled condition node.</summary>
internal sealed class Cond
{
    public ConditionDef Def = null!;
    public LuaRef Lua = LuaRef.None;
    public Func<double>? Sensor;
    public Cond[] Children = [];
}

/// <summary>Runtime of one transition.</summary>
internal sealed class TransitionRuntime
{
    public TransitionDef Def = null!;
    public Cond Cond = null!;
    public int Index;
    public long HoldSince = -1, LastFired = -1;
    public bool? LastResult;
    public double? LastValue;
}

/// <summary>Runtime of one state (enter/tick/exit refs, progress tracking).</summary>
public sealed class StateRuntime
{
    internal StateDef Def = null!;
    internal MachineRuntime Owner = null!;
    internal LuaRef Enter = LuaRef.None, TickFn = LuaRef.None, Exit = LuaRef.None, ProgressLua = LuaRef.None;
    internal Func<double>? ProgressSensor;
    internal TransitionRuntime[] Trans = [];
    internal readonly Dictionary<string, MachineRuntime> SubCache = new();
    /// <summary>Name.</summary>
    public string Name { get; internal set; } = "";
    /// <summary>Entered at (ms).</summary>
    public long EnteredAt { get; internal set; }
    /// <summary>Best progress and when (ms).</summary>
    public double Best { get; internal set; } = double.NegativeInfinity;
    /// <summary>When the best progress was reached (ms).</summary>
    public long BestAt { get; internal set; }
    /// <summary>Sub-machine, if any (active one).</summary>
    public MachineRuntime? Sub { get; internal set; }
    /// <summary>Budget (ms without progress before `stuck`).</summary>
    public int BudgetMs => Def.BudgetMs;
}

/// <summary>Runtime of a machine: states, the active one.</summary>
public sealed class MachineRuntime
{
    internal MachineDef Def = null!;
    internal Dictionary<string, StateRuntime> States = new();
    /// <summary>Name (layer name for the root, machine name for `use`).</summary>
    public string Name { get; internal set; } = "";
    /// <summary>Active state.</summary>
    public StateRuntime? Active { get; internal set; }
}

/// <summary>Runtime of a layer: its machine, rate gating, claims.</summary>
public sealed class LayerRuntime
{
    internal LayerDef Def = null!;
    internal MachineRuntime Root = null!;
    internal long LastRun = -1, LastRunTick = -1;
    internal readonly long[] EventTotals = new long[64];
    internal readonly List<ConditionTrace> Traces = new();
    internal readonly List<StateRuntime> Path = new();
    internal int BudgetStrikes;
    /// <summary>Name.</summary>
    public string Name => Def.Name;
    /// <summary>Layer index in the action system (1 = highest brain layer; 0 is the operator).</summary>
    public byte Index { get; internal set; }
    /// <summary>Claims of the active path.</summary>
    public Resource Claims { get; internal set; }
    /// <summary>Is the layer due this tick (rate)?</summary>
    public bool Due(long nowMs) => Def.Enabled && (Def.Rate.EveryTick || LastRun < 0 || nowMs - LastRun >= Def.Rate.Ms);
    /// <summary>The active path, outermost first.</summary>
    public IReadOnlyList<StateRuntime> ActivePath
    {
        get { var l = new List<StateRuntime>(); for (var s = Root.Active; s is not null; s = s.Sub?.Active) l.Add(s); return l; }
    }
}

/// <summary>One bot's brain: engine, layers, blackboard, debug tap. Implements the agent's tick step 6.</summary>
public sealed class BrainInstance : IBrainTicker, IDisposable
{
    private CompiledBrain _brain;
    private NativeLuaEngine _engine;
    private ApiModule[] _modules = [];
    private readonly ApiErrors _errors = new();
    private readonly int[] _arena = new int[8192];
    private SensorRegistry _sensors = new();
    private readonly List<LayerRuntime> _layers = new();
    private readonly AgentContext _ctx;
    private long _lastInstr;
    private long _now;
    private bool _suspended;
    /// <summary>Resolves a version hash for hot swaps (set by the supervisor).</summary>
    public Func<string, CompiledBrain?>? Resolver { get; set; }
    /// <summary>Raised after a swap (supervisor records the assignment).</summary>
    public Action<string, string>? Swapped { get; set; }

    /// <summary>Build from a compiled brain version.</summary>
    public BrainInstance(CompiledBrain brain, AgentContext ctx)
    {
        _brain = brain; _ctx = ctx;
        _engine = new NativeLuaEngine();
        Build(brain, null);
    }

    /// <summary>Version hash.</summary>
    public string VersionHash => _brain.Hash;
    /// <summary>Brain name.</summary>
    public string BrainName => _brain.Definition.Brain;
    /// <summary>The compiled version.</summary>
    public CompiledBrain Compiled => _brain;
    /// <summary>Layers, highest priority first.</summary>
    public IReadOnlyList<LayerRuntime> Layers => _layers;
    /// <summary>The engine.</summary>
    public NativeLuaEngine Engine => _engine;
    /// <summary>Sensors.</summary>
    public SensorRegistry Sensors => _sensors;
    /// <summary>Debug tap.</summary>
    public DebugTap Debug { get; } = new();
    /// <summary>Suspended after three budget overruns in a row (the operator resumes).</summary>
    public bool Suspended => _suspended;
    /// <summary>Build / load errors (a brain with errors does not tick).</summary>
    public List<string> LoadErrors { get; } = new();
    /// <inheritdoc/>
    public long LastInstructions => _lastInstr;
    private static readonly int EventKindCount = Math.Min(64, Enum.GetValues<EventKind>().Length);

    private void Build(CompiledBrain brain, BrainCheckpoint? keep)
    {
        LoadErrors.Clear();
        _modules = ApiRegistry.CreateModules(_ctx);
        foreach (var m in _modules) if (m is DbgModule d) d.Sink = Debug;
        _engine.RegisterApi(_modules, _errors);
        var sources = new Dictionary<string, string>(brain.Modules);
        _engine.SetSources(sources);
        ApiScope.Bind(_modules, _arena, _errors, 1);
        foreach (var lib in BuiltinLibraries.Sources.Keys) _engine.LoadModule(lib);
        foreach (var m in brain.Definition.Modules)
            if (_engine.LoadModule(m).Error is { } err) LoadErrors.Add(err);
        foreach (var m in brain.Modules.Keys)
            if (!brain.Definition.Modules.Any(x => BrainLinter.StripPin(x) == m) && _engine.LoadModule(m).Error is { } err) LoadErrors.Add(err);
        _sensors = SensorRegistry.Create(_modules);
        foreach (var kv in brain.Definition.Blackboard)
            if (kv.Value is string json) _engine.Set("bot", null, kv.Key, json);
        _layers.Clear();
        var ordered = brain.Definition.Layers.Select((l, i) => (l, i)).OrderByDescending(x => x.l.Priority).ThenBy(x => x.i).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var def = ordered[i].l;
            var lr = new LayerRuntime { Def = def, Index = (byte)(i + 1) };
            lr.Root = NewMachine(def.Machine, def.Name);
            _layers.Add(lr);
        }
        foreach (var e in LoadErrors) _ctx.Log.Text(LogCategory.Brain, LogLevel.Crit, $"brain {brain.Definition.Brain}@{brain.Hash}: {e}");
        _now = _ctx.Now;
        foreach (var lr in _layers)
        {
            _engine.BindLayer(lr.Name);
            ApiScope.Bind(_modules, _arena, _errors, lr.Index);
            var initial = keep?.LayerPaths.GetValueOrDefault(lr.Name);
            EnterMachine(lr, lr.Root, initial, 0, "start");
        }
    }

    private MachineRuntime NewMachine(MachineDef def, string name)
    {
        var m = new MachineRuntime { Def = def, Name = name };
        foreach (var (sn, sd) in def.States)
        {
            var s = new StateRuntime { Def = sd, Name = sn, Owner = m };
            s.Enter = Fn(sd.Enter, $"{name}.{sn} enter"); s.TickFn = Fn(sd.Tick, $"{name}.{sn} tick"); s.Exit = Fn(sd.Exit, $"{name}.{sn} exit");
            if (sd.Progress is { } p)
            {
                if (_sensors.Get(p) is { } sens) s.ProgressSensor = sens;
                else s.ProgressLua = Fn(p, $"{name}.{sn} progress");
            }
            var ts = sd.Transitions.Select((t, i) => new TransitionRuntime { Def = t, Index = i, Cond = Compile(t.Condition, $"{name}.{sn} -> {t.To}") })
                .OrderByDescending(t => t.Def.Priority).ThenBy(t => t.Index).ToArray();
            s.Trans = ts;
            m.States[sn] = s;
        }
        return m;
    }

    private LuaRef Fn(string? code, string where)
    {
        if (code is null) return LuaRef.None;
        var (fn, err) = _engine.Resolve(code);
        if (err is not null) LoadErrors.Add($"{where}: {err}");
        return fn;
    }

    private Cond Compile(ConditionDef d, string where)
    {
        var c = new Cond { Def = d };
        switch (d.Kind)
        {
            case ConditionKind.Lua: c.Lua = Fn(d.Lua, where); break;
            case ConditionKind.Value:
                c.Sensor = _sensors.Get(d.Sensor ?? "");
                if (c.Sensor is null) { c.Lua = Fn(d.Sensor, where); }
                break;
            case ConditionKind.All or ConditionKind.Any or ConditionKind.Not:
                // fixed leaves first, Lua leaves last (short-circuit)
                c.Children = (d.Children ?? []).Select(x => Compile(x, where)).OrderBy(x => x.Def.Kind == ConditionKind.Lua ? 1 : 0).ToArray();
                break;
        }
        return c;
    }

    // ---- entering / exiting

    private string ResolveUse(string use)
    {
        if (!use.Contains("${")) return use;
        var sb = new StringBuilder();
        for (var i = 0; i < use.Length; i++)
        {
            if (use[i] == '$' && i + 1 < use.Length && use[i + 1] == '{')
            {
                var j = use.IndexOf('}', i);
                var expr = use[(i + 2)..j];
                var v = _sensors.Read(expr);
                sb.Append(double.IsNaN(v) ? "?" : ((long)v).ToString(CultureInfo.InvariantCulture));
                i = j;
            }
            else sb.Append(use[i]);
        }
        return sb.ToString();
    }

    private string? SelectInitial(MachineDef def)
    {
        if (def.Select is not { } sel) return null;
        var self = _ctx.World.Self;
        string key = sel.By switch
        {
            SelectBy.ClassFamily => self.ClassFamily.ToString(CultureInfo.InvariantCulture),
            SelectBy.Map => _ctx.World.Map.MapName,
            _ => self.Level.ToString(CultureInfo.InvariantCulture),
        };
        if (sel.By == SelectBy.LevelBand)
        {
            // cases "1-20": state
            foreach (var (k, v) in sel.Cases)
            {
                var dash = k.IndexOf('-');
                if (dash > 0 && int.TryParse(k[..dash], out var lo) && int.TryParse(k[(dash + 1)..], out var hi) && self.Level >= lo && self.Level <= hi) return v;
            }
            return sel.Cases.GetValueOrDefault("default");
        }
        return sel.Cases.GetValueOrDefault(key) ?? sel.Cases.GetValueOrDefault("default");
    }

    private void EnterMachine(LayerRuntime lr, MachineRuntime m, IReadOnlyList<string>? keepPath, int depth, string why)
    {
        var name = keepPath is { } kp && depth < kp.Count && m.States.ContainsKey(kp[depth]) ? kp[depth] : SelectInitial(m.Def) ?? m.Def.Initial;
        if (!m.States.TryGetValue(name, out var s)) return;
        EnterState(lr, s, keepPath, depth);
    }

    private void EnterState(LayerRuntime lr, StateRuntime s, IReadOnlyList<string>? keepPath, int depth)
    {
        s.Owner.Active = s;
        s.EnteredAt = _now; s.Best = double.NegativeInfinity; s.BestAt = _now;
        foreach (var t in s.Trans) { t.HoldSince = -1; t.LastResult = null; t.LastValue = null; }
        if (s.Enter.Ok) RunLua(lr, s, s.Enter, "enter");
        // sub-machine: inline, or `use` (resolved on entry)
        MachineDef? subDef = s.Def.Machine;
        var subName = s.Name;
        if (subDef is null && s.Def.Use is { } use)
        {
            subName = ResolveUse(use);
            if (!_brain.Definition.Machines.TryGetValue(subName, out subDef))
                LogError(lr, s, $"use: no machine '{subName}'");
        }
        if (subDef is not null)
        {
            if (!s.SubCache.TryGetValue(subName, out var sub)) s.SubCache[subName] = sub = NewMachine(subDef, subName);
            s.Sub = sub;
            EnterMachine(lr, sub, keepPath, depth + 1, "enter");
        }
        else s.Sub = null;
    }

    private void ExitDownFrom(LayerRuntime lr, StateRuntime s)
    {
        if (s.Sub?.Active is { } child) ExitDownFrom(lr, child);
        if (s.Exit.Ok) RunLua(lr, s, s.Exit, "exit");
        _engine.ClearState(lr.Name);
    }

    // ---- the tick

    /// <inheritdoc/>
    public void Tick(AgentContext ctx)
    {
        _now = ctx.Now;
        _lastInstr = 0;
        Debug.BeginTick();
        if (_suspended || LoadErrors.Count > 0) return;
        for (var i = 0; i < _layers.Count; i++)
        {
            var lr = _layers[i];
            if (!lr.Due(_now)) continue;
            ApiScope.Bind(_modules, _arena, _errors, lr.Index);
            _engine.BindLayer(lr.Name);
            RunLayer(lr, ctx);
            lr.LastRun = _now; lr.LastRunTick = ctx.Tick;
            for (var k = 0; k < EventKindCount; k++) lr.EventTotals[k] = ctx.Events.Total((EventKind)k);
            if (_suspended) break;
        }
    }

    private void CollectPath(LayerRuntime lr)
    {
        lr.Path.Clear();
        for (var s = lr.Root.Active; s is not null; s = s.Sub?.Active) lr.Path.Add(s);
    }

    private void RunLayer(LayerRuntime lr, AgentContext ctx)
    {
        CollectPath(lr);
        if (lr.Path.Count == 0) return;
        // progress tracking along the path
        for (var d = 0; d < lr.Path.Count; d++) UpdateProgress(lr, lr.Path[d]);
        var watched = Debug.Watched;
        if (watched) lr.Traces.Clear();
        var fired = false;
        for (var d = 0; d < lr.Path.Count && !fired; d++)
        {
            var s = lr.Path[d];
            var inDwell = s.Def.MinDwellMs > 0 && _now - s.EnteredAt < s.Def.MinDwellMs;
            var stuck = IsStuck(s);
            for (var ti = 0; ti < s.Trans.Length; ti++)
            {
                var t = s.Trans[ti];
                if (inDwell && t.Cond.Def.Kind != ConditionKind.Stuck) { if (watched) Trace(lr, s, d, t, null, null); continue; }
                if (t.Def.CooldownMs > 0 && t.LastFired >= 0 && _now - t.LastFired < t.Def.CooldownMs) { if (watched) Trace(lr, s, d, t, false, null); continue; }
                double? value = null;
                var r = Eval(t.Cond, lr, s, stuck, ref value);
                t.LastResult = r; t.LastValue = value;
                var hold = false;
                if (r)
                {
                    if (t.HoldSince < 0) t.HoldSince = _now;
                    hold = t.Def.HoldMs > 0 && _now - t.HoldSince < t.Def.HoldMs;
                }
                else t.HoldSince = -1;
                if (watched) Trace(lr, s, d, t, r, value);
                if (r && !hold)
                {
                    Fire(lr, s, d, t, ctx, value);
                    fired = true;
                    break;
                }
            }
        }
        if (!fired)
        {
            var leaf = lr.Path[^1];
            if (leaf.TickFn.Ok) RunLua(lr, leaf, leaf.TickFn, "tick");
        }
        else CollectPath(lr);
        var claims = Resource.None;
        foreach (var s in lr.Path) claims |= s.Def.Claims;
        lr.Claims = claims;
        ctx.Actions.SetClaims(lr.Index, claims);
    }

    private void Trace(LayerRuntime lr, StateRuntime s, int depth, TransitionRuntime t, bool? r, double? v)
        => lr.Traces.Add(new ConditionTrace(t.Def.To, KindName(t.Cond.Def.Kind), t.Cond.Def.Describe(), v, r,
            t.HoldSince < 0 ? 0 : (int)(_now - t.HoldSince), s.Name, depth, t.Index, t.Def.HoldMs));

    private static string KindName(ConditionKind k) => k switch { ConditionKind.ActionResult => "action", _ => k.ToString().ToLowerInvariant() };

    private void UpdateProgress(LayerRuntime lr, StateRuntime s)
    {
        double v;
        if (s.ProgressSensor is { } f) v = f();
        else if (s.ProgressLua.Ok)
        {
            var r = RunLua(lr, s, s.ProgressLua, "progress");
            if (r.Status != LuaCallStatus.Ok) return;
            v = r.Number;
        }
        else return;
        if (double.IsNaN(v)) return;
        if (v > s.Best) { s.Best = v; s.BestAt = _now; }
    }

    private bool IsStuck(StateRuntime s)
    {
        if (s.Def.TimeoutMs is { } to && _now - s.EnteredAt >= to) return true;
        if (s.Def.BudgetMs <= 0) return false;
        var since = s.ProgressSensor is not null || s.ProgressLua.Ok ? s.BestAt : s.EnteredAt;
        return _now - since >= s.Def.BudgetMs;
    }

    private bool Eval(Cond c, LayerRuntime lr, StateRuntime s, bool stuck, ref double? value)
    {
        var d = c.Def;
        switch (d.Kind)
        {
            case ConditionKind.Always: return true;
            case ConditionKind.Stuck: return stuck;
            case ConditionKind.After: value = _now - s.EnteredAt; return _now - s.EnteredAt >= d.Ms;
            case ConditionKind.Every: return _now - s.EnteredAt >= d.Ms;
            case ConditionKind.Event:
            {
                var k = (int)d.Event!.Value;
                var n = _ctx.Events.Total(d.Event.Value);
                return lr.Def.Rate.EveryTick ? _ctx.Events.Fired(d.Event.Value) : n > lr.EventTotals[k];
            }
            case ConditionKind.Value:
            {
                double v;
                if (c.Sensor is { } f) v = f();
                else if (c.Lua.Ok) { var r = RunLua(lr, s, c.Lua, "value"); if (r.Status != LuaCallStatus.Ok) return false; v = r.Number; }
                else return false;
                value = v;
                if (double.IsNaN(v)) return false;
                return d.Op switch
                {
                    CompareOp.Lt => v < d.Than, CompareOp.Le => v <= d.Than, CompareOp.Gt => v > d.Than,
                    CompareOp.Ge => v >= d.Than, CompareOp.Eq => Math.Abs(v - d.Than) < 1e-9, _ => Math.Abs(v - d.Than) >= 1e-9,
                };
            }
            case ConditionKind.Lua:
            {
                if (!c.Lua.Ok) return false;
                var r = RunLua(lr, s, c.Lua, "condition");
                if (r.IsNumber) value = r.Number;
                return r.Status == LuaCallStatus.Ok && r.Truthy;
            }
            case ConditionKind.ActionResult:
            {
                var id = _engine.GetNumber("state", lr.Name, d.ActionName ?? "");
                if (double.IsNaN(id)) return false;
                var rec = _ctx.Actions.Get(new ActionId((int)id));
                if (rec.Id == 0 || rec.Pending) return false;
                value = id;
                return (d.Result ?? "acked").ToLowerInvariant() switch
                {
                    "resolved" or "done" => true,
                    "acked" or "ok" => rec.State == ActionState.Acked,
                    "refused" => rec.State == ActionState.Refused,
                    "timedout" => rec.State == ActionState.TimedOut,
                    "failed" => rec.State is ActionState.Failed or ActionState.Refused or ActionState.TimedOut or ActionState.Cancelled,
                    "cancelled" => rec.State == ActionState.Cancelled,
                    _ => false,
                };
            }
            case ConditionKind.Procedure:
            {
                if (_ctx.Events.Last(EventKind.ProcedureDone) is not { } e || e.Tick <= lr.LastRunTick || e.Arg1 != (long)d.Procedure!.Value) return false;
                var ok = e.Arg2 == 1;
                return (d.Result ?? "ok").ToLowerInvariant() switch { "ok" or "succeeded" or "acked" => ok, "failed" => !ok, _ => true };
            }
            case ConditionKind.Job:
            {
                var t = _engine.GetNumber("state", lr.Name, d.Job ?? "");
                if (double.IsNaN(t) || !_ctx.Jobs.IsDone(new JobTicket((int)t))) return false;
                value = t;
                var res = _ctx.Jobs.Result(new JobTicket((int)t));
                var ok = res is not Exception && !(res is PathResult { Status: PathStatus.Failed });
                return (d.Result ?? "ok").ToLowerInvariant() switch { "ok" or "done" => ok, "failed" => !ok, _ => true };
            }
            case ConditionKind.All:
                foreach (var ch in c.Children) { double? dummy = null; if (!Eval(ch, lr, s, stuck, ref dummy)) return false; }
                return true;
            case ConditionKind.Any:
                foreach (var ch in c.Children) { double? dummy = null; if (Eval(ch, lr, s, stuck, ref dummy)) return true; }
                return false;
            case ConditionKind.Not:
            {
                double? dummy = null;
                return c.Children.Length > 0 && !Eval(c.Children[0], lr, s, stuck, ref dummy);
            }
        }
        return false;
    }

    private void Fire(LayerRuntime lr, StateRuntime src, int depth, TransitionRuntime t, AgentContext ctx, double? value)
    {
        t.LastFired = _now;
        var leaf = lr.Path[^1].Name;
        var fromPath = string.Join('/', lr.Path.Take(depth + 1).Select(x => x.Name));
        var to = t.Def.To;
        if (t.Cond.Def.Kind == ConditionKind.Stuck) ctx.Events.Raise(EventKind.Stuck, lr.Index, depth);
        var reason = Reason(t, value);
        // exit leaf .. source, then enter the target
        ExitDownFrom(lr, src);
        StateRuntime? target;
        if (to == "^")
        {
            var parent = depth > 0 ? lr.Path[depth - 1] : null;
            if (parent is null) target = src;
            else { ExitDownFrom(lr, parent); target = parent; }
        }
        else if (to.Contains(':'))
        {
            var mn = to[..to.IndexOf(':')]; var sn = to[(to.IndexOf(':') + 1)..];
            MachineRuntime? m = null;
            for (var i = 0; i < lr.Path.Count; i++) if (lr.Path[i].Owner.Name == mn) { m = lr.Path[i].Owner; break; }
            if (m is null && _brain.Definition.Machines.TryGetValue(mn, out var md))
            {
                // switch the layer's root machine
                ExitDownFrom(lr, lr.Root.Active!);
                lr.Root = NewMachine(md, mn);
                _engine.ClearLayer(lr.Name);
                m = lr.Root;
            }
            target = m?.States.GetValueOrDefault(sn);
        }
        else target = src.Owner.States.GetValueOrDefault(to);
        if (target is null) { LogError(lr, src, $"transition target '{to}' not found"); target = src; }
        EnterState(lr, target, null, depth);
        CollectPath(lr);
        var rec = new TransitionRecord(ctx.Tick, SystemClock.ToUtc(_now), lr.Name, fromPath, to, KindName(t.Cond.Def.Kind), reason,
            value is { } v ? new Dictionary<string, double> { ["value"] = v } : new Dictionary<string, double>(), depth, t.Index, leaf);
        Debug.Transition(rec);
        if (ctx.Log.IsEnabled(LogCategory.Brain, LogLevel.Info))
            ctx.Log.Log(LogCategory.Brain, LogLevel.Info, $"{lr.Name}: {fromPath} -> {to} ({rec.Kind}: {reason})");
    }

    private string Reason(TransitionRuntime t, double? value)
    {
        var tpl = t.Def.Reason;
        if (string.IsNullOrEmpty(tpl)) return t.Cond.Def.Describe() + (value is { } v ? $" ({v.ToString("0.##", CultureInfo.InvariantCulture)})" : "");
        var sb = new StringBuilder();
        for (var i = 0; i < tpl.Length; i++)
        {
            if (tpl[i] != '{') { sb.Append(tpl[i]); continue; }
            var j = tpl.IndexOf('}', i);
            if (j < 0) { sb.Append(tpl[i..]); break; }
            var v = _sensors.Read(tpl[(i + 1)..j]);
            sb.Append(double.IsNaN(v) ? "?" : v.ToString("0.#", CultureInfo.InvariantCulture));
            i = j;
        }
        return sb.ToString();
    }

    private LuaCallResult RunLua(LayerRuntime lr, StateRuntime s, LuaRef fn, string what)
    {
        var r = _engine.Call(fn, lr.Def.BudgetMs);
        _lastInstr += _engine.LastInstructions;
        if (r.Status == LuaCallStatus.Ok) { if (what == "tick") lr.BudgetStrikes = 0; return r; }
        LogError(lr, s, $"{what}: {r.Error}");
        if (r.Status == LuaCallStatus.Budget)
        {
            _ctx.Events.Raise(EventKind.Budget, lr.Index);
            if (++lr.BudgetStrikes >= 3)
            {
                _suspended = true;
                _ctx.Log.Text(LogCategory.Brain, LogLevel.Crit, $"brain suspended: layer {lr.Name} state {s.Name} over its {lr.Def.BudgetMs} ms budget three times in a row");
            }
        }
        if (s.Def.OnError is { } onErr && s.Owner.States.TryGetValue(onErr, out var target) && what == "tick")
        {
            ExitDownFrom(lr, s);
            EnterState(lr, target, null, lr.Path.IndexOf(s));
        }
        return r;
    }

    private void LogError(LayerRuntime lr, StateRuntime s, string err)
    {
        Debug.Error(new ErrorTrace(_ctx.Tick, SystemClock.ToUtc(_now), lr.Name, s.Name, err));
        _ctx.Log.Log(LogCategory.Brain, LogLevel.Note, $"{lr.Name}/{s.Name}: {err}");
    }

    // ---- operator, swap, checkpoint

    /// <inheritdoc/>
    public object? Control(ControlCommand cmd, AgentContext ctx)
    {
        _now = ctx.Now;
        switch (cmd.Kind)
        {
            case ControlKind.ForceState:
            {
                var lr = _layers.FirstOrDefault(l => l.Name == cmd.Layer) ?? throw new ArgumentException($"no layer {cmd.Layer}");
                var path = (cmd.State ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (path.Length == 0 || !lr.Root.States.ContainsKey(path[0])) throw new ArgumentException($"no state {cmd.State} in layer {cmd.Layer}");
                CollectPath(lr);
                var from = string.Join('/', lr.Path.Select(p => p.Name));
                ApiScope.Bind(_modules, _arena, _errors, lr.Index);
                _engine.BindLayer(lr.Name);
                if (lr.Root.Active is { } a) ExitDownFrom(lr, a);
                EnterState(lr, lr.Root.States[path[0]], path, 0);
                CollectPath(lr);
                var rec = new TransitionRecord(ctx.Tick, SystemClock.ToUtc(_now), lr.Name, from, string.Join('/', path), "operator", $"operator ({cmd.By})",
                    new Dictionary<string, double>(), 0, -1, from.Split('/').LastOrDefault() ?? "");
                Debug.Transition(rec);
                ctx.Log.Text(LogCategory.Brain, LogLevel.Note, $"operator forced {lr.Name} -> {cmd.State}");
                return rec;
            }
            case ControlKind.SetBlackboard:
                _engine.Set(cmd.Scope ?? "bot", cmd.Layer, cmd.Key ?? "", cmd.Value ?? "null");
                ctx.Log.Text(LogCategory.Brain, LogLevel.Note, $"operator set bb.{cmd.Scope}{(cmd.Layer is null ? "" : "." + cmd.Layer)}.{cmd.Key} = {cmd.Value}");
                return BlackboardJson();
            case ControlKind.ApplyVersion:
            {
                var next = Resolver?.Invoke(cmd.VersionHash ?? "") ?? throw new ArgumentException($"unknown version {cmd.VersionHash}");
                var from = VersionHash;
                Swap(next, cmd.Value != "false");
                Swapped?.Invoke(from, next.Hash);
                return new { from, to = next.Hash };
            }
            case ControlKind.Resume:
                _suspended = false;
                foreach (var l in _layers) l.BudgetStrikes = 0;
                return null;
        }
        return null;
    }

    /// <summary>Hot-swap to a new version (at a tick boundary: called from the tick's control step), keeping bb.bot
    /// and, when <paramref name="keepState"/>, the active states that still exist by name.</summary>
    public void Swap(CompiledBrain next, bool keepState)
    {
        var cp = Checkpoint();
        _engine.Dispose();
        _engine = new NativeLuaEngine();
        _brain = next;
        Build(next, keepState ? cp : null);
        if (JsonNode.Parse(cp.BotBlackboardJson) is JsonObject bot) _engine.RestoreBot(bot);
        SeedBlackboardDefaults();
        _suspended = false;
        _ctx.Log.Text(LogCategory.Brain, LogLevel.Note, $"brain swapped to {next.Definition.Brain}@{next.Hash} (keepState={keepState})");
    }

    // the definition's `blackboard:` values fill keys the restored bb.bot does not have (a kept bb.bot must not hide a
    // new version's defaults)
    private void SeedBlackboardDefaults()
    {
        foreach (var kv in _brain.Definition.Blackboard)
            if (kv.Value is string json && !_engine.Has("bot", null, kv.Key)) _engine.Set("bot", null, kv.Key, json);
    }

    /// <summary>Checkpoint: active state paths + bb.bot.</summary>
    public BrainCheckpoint Checkpoint()
    {
        var paths = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var lr in _layers) { CollectPath(lr); paths[lr.Name] = lr.Path.Select(p => p.Name).ToList(); }
        return new BrainCheckpoint(_ctx.BotId, SystemClock.ToUtc(_ctx.Now), VersionHash, paths, _engine.Snapshot("bot", null, 1 << 16).ToJsonString());
    }

    /// <summary>Resume from a checkpoint (same or compatible version).</summary>
    public void Resume(BrainCheckpoint checkpoint)
    {
        if (JsonNode.Parse(checkpoint.BotBlackboardJson) is JsonObject bot) _engine.RestoreBot(bot);
        SeedBlackboardDefaults();
        foreach (var lr in _layers)
            if (checkpoint.LayerPaths.TryGetValue(lr.Name, out var p) && p.Count > 0 && lr.Root.States.ContainsKey(p[0]))
            {
                ApiScope.Bind(_modules, _arena, _errors, lr.Index);
                _engine.BindLayer(lr.Name);
                if (lr.Root.Active is { } a) ExitDownFrom(lr, a);
                EnterState(lr, lr.Root.States[p[0]], p, 0);
            }
    }

    /// <summary>The three blackboard scopes as one JSON object {bot, layer: {name: {}}, state: {name: {}}}.</summary>
    public JsonObject BlackboardJson()
    {
        var layer = new JsonObject(); var state = new JsonObject();
        foreach (var lr in _layers) { layer[lr.Name] = _engine.Snapshot("layer", lr.Name); state[lr.Name] = _engine.Snapshot("state", lr.Name); }
        return new JsonObject { ["bot"] = _engine.Snapshot("bot", null), ["layer"] = layer, ["state"] = state };
    }

    /// <summary>Build a debug frame (tick thread; allocates, called at the publish rate only).</summary>
    public DebugFrame Frame(AgentContext ctx, TickStats stats, bool paused)
    {
        var layers = new List<LayerTrace>();
        foreach (var lr in _layers)
        {
            CollectPath(lr);
            var leaf = lr.Path.Count > 0 ? lr.Path[^1] : null;
            var top = lr.Path.Count > 0 ? lr.Path[0] : null;
            var withProgress = lr.Path.LastOrDefault(p => p.ProgressSensor is not null || p.ProgressLua.Ok);
            layers.Add(new LayerTrace(lr.Name, lr.Def.Priority, lr.Path.Select(p => p.Name).ToList(),
                leaf is null ? null : SystemClock.ToUtc(leaf.EnteredAt), leaf is null ? 0 : ctx.Now - leaf.EnteredAt,
                lr.Path.Select(p => SystemClock.ToUtc(p.EnteredAt)).ToList(),
                withProgress is null || double.IsNegativeInfinity(withProgress.Best) ? null : withProgress.Best,
                withProgress is null ? null : SystemClock.ToUtc(withProgress.BestAt), leaf?.BudgetMs ?? 0,
                Enum.GetValues<Resource>().Where(r => r is not Resource.None and not Resource.All && (lr.Claims & r) != 0).Select(r => r.ToString()).ToList(),
                lr.Traces.ToList()));
        }
        var actions = ctx.Actions.Recent(20).Select(a => new ActionTrace(a.Id, LuaConstants.Upper(a.Kind.ToString()), a.State.ToString().ToLowerInvariant(), a.Code,
            ErrorCodes.Describe(a.Code), SystemClock.ToUtc(a.SentAt == 0 ? a.QueuedAt : a.SentAt), a.Pending ? null : SystemClock.ToUtc(a.ResolvedAt),
            a.Layer == 0 ? "operator" : _layers.FirstOrDefault(l => l.Index == a.Layer)?.Name ?? $"layer {a.Layer}")).ToList();
        var procs = ctx.Procedures.Active.Select(p => new ProcedureTrace(LuaConstants.Upper(p.Kind.ToString()), p.Step, SystemClock.ToUtc(p.StepSince))).ToList();
        var steps = new Dictionary<string, double>();
        Span<TickSample> last = stackalloc TickSample[1];
        if (stats.Last(last) == 1)
        {
            var s = last[0];
            steps["control"] = s.ControlUs; steps["apply"] = s.ApplyUs; steps["actions"] = s.ActionsUs; steps["move"] = s.MoveUs;
            steps["combat"] = s.CombatUs; steps["brain"] = s.BrainUs; steps["publish"] = s.PublishUs;
        }
        var tick = new TickSummary(stats.RecentQuantile(x => x.TotalUs, 0.5, 200), stats.RecentQuantile(x => x.TotalUs, 0.99, 200),
            stats.RecentQuantile(x => x.AllocBytes, 0.95, 200), steps);
        var trans = Debug.Transitions();
        return new DebugFrame(ctx.BotId, ctx.Tick, SystemClock.ToUtc(ctx.Now), $"{BrainName}@{VersionHash}", paused || _suspended, layers,
            trans.Skip(Math.Max(0, trans.Count - 20)).Reverse().ToList(), BlackboardJson(), Debug.Watches(), actions, procs, tick,
            Debug.Errors().Reverse().Take(10).ToList());
    }

    /// <inheritdoc/>
    public void Dispose() => _engine.Dispose();
}
