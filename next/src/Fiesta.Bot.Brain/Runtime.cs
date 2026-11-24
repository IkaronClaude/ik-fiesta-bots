using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;

namespace Fiesta.Bot.Brain;

/// <summary>A compiled Lua function reference inside one VM (registry index).</summary>
public readonly record struct LuaRef(int Value)
{
    /// <summary>No function.</summary>
    public static readonly LuaRef None = new(-1);
}

/// <summary>Outcome of one protected Lua call.</summary>
public enum LuaCallStatus : byte { Ok, Error, Budget, ArgError }

/// <summary>Result of a protected call: status, truthiness of the first result, error text.</summary>
public readonly record struct LuaCallResult(LuaCallStatus Status, bool Truthy, double Number, string? Error);

/// <summary>One Lua VM for one bot (native Lua 5.4 or the MoonSharp compatibility engine).</summary>
public interface ILuaEngine : IDisposable
{
    /// <summary>Engine name.</summary>
    string Name { get; }
    /// <summary>Register all API modules (generated bindings) and constants.</summary>
    void RegisterApi(IReadOnlyList<ApiModule> modules, ApiErrors errors);
    /// <summary>Compile and run a module chunk; returns its table as a reference, or an error.</summary>
    (LuaRef Module, string? Error) LoadModule(string path, string source);
    /// <summary>Resolve `module.fn` (or compile an inline expression/body) to a function reference.</summary>
    (LuaRef Fn, string? Error) Resolve(string fnRefOrExpression);
    /// <summary>Call a function inside a coroutine with an instruction budget.</summary>
    LuaCallResult Call(LuaRef fn, long instructionBudget);
    /// <summary>Lua heap size in bytes.</summary>
    long HeapBytes { get; }
    /// <summary>Instructions executed by the last call.</summary>
    long LastInstructions { get; }
    /// <summary>Snapshot a blackboard scope table to JSON (for the debugger and checkpoints).</summary>
    string SnapshotTable(LuaRef table, int maxBytes);
    /// <summary>Restore a blackboard scope from JSON.</summary>
    void RestoreTable(LuaRef table, string json);
}

/// <summary>Native Lua 5.4.9 via KeraLua with function-pointer bindings (docs/LUA_BENCH.md).</summary>
public sealed class NativeLuaEngine : ILuaEngine
{
    /// <summary>Create a sandboxed VM with a memory cap.</summary>
    public NativeLuaEngine(long memoryCapBytes = 16 << 20) => throw new NotImplementedException();
    /// <inheritdoc/>
    public string Name => "lua54";
    /// <inheritdoc/>
    public void RegisterApi(IReadOnlyList<ApiModule> modules, ApiErrors errors) => throw new NotImplementedException();
    /// <inheritdoc/>
    public (LuaRef Module, string? Error) LoadModule(string path, string source) => throw new NotImplementedException();
    /// <inheritdoc/>
    public (LuaRef Fn, string? Error) Resolve(string fnRefOrExpression) => throw new NotImplementedException();
    /// <inheritdoc/>
    public LuaCallResult Call(LuaRef fn, long instructionBudget) => throw new NotImplementedException();
    /// <inheritdoc/>
    public long HeapBytes => throw new NotImplementedException();
    /// <inheritdoc/>
    public long LastInstructions => throw new NotImplementedException();
    /// <inheritdoc/>
    public string SnapshotTable(LuaRef table, int maxBytes) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void RestoreTable(LuaRef table, string json) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Dispose() => throw new NotImplementedException();
}

/// <summary>MoonSharp engine for the API-2 compatibility shim during migration.</summary>
public sealed class MoonSharpCompatEngine : ILuaEngine
{
    /// <summary>Create.</summary>
    public MoonSharpCompatEngine() => throw new NotImplementedException();
    /// <inheritdoc/>
    public string Name => "moonsharp";
    /// <inheritdoc/>
    public void RegisterApi(IReadOnlyList<ApiModule> modules, ApiErrors errors) => throw new NotImplementedException();
    /// <inheritdoc/>
    public (LuaRef Module, string? Error) LoadModule(string path, string source) => throw new NotImplementedException();
    /// <inheritdoc/>
    public (LuaRef Fn, string? Error) Resolve(string fnRefOrExpression) => throw new NotImplementedException();
    /// <inheritdoc/>
    public LuaCallResult Call(LuaRef fn, long instructionBudget) => throw new NotImplementedException();
    /// <inheritdoc/>
    public long HeapBytes => throw new NotImplementedException();
    /// <inheritdoc/>
    public long LastInstructions => throw new NotImplementedException();
    /// <inheritdoc/>
    public string SnapshotTable(LuaRef table, int maxBytes) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void RestoreTable(LuaRef table, string json) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Dispose() => throw new NotImplementedException();
}

/// <summary>Resolves `require` paths to the module versions pinned by a brain version.</summary>
public sealed class ModuleResolver(IReadOnlyDictionary<string, string> pinnedSources)
{
    /// <summary>Pinned module sources by path.</summary>
    public IReadOnlyDictionary<string, string> Sources { get; } = pinnedSources;
    /// <summary>Source for a `require` path, null if not pinned (load error).</summary>
    public string? Find(string path) => throw new NotImplementedException();
}

/// <summary>Blackboard scopes (Lua tables bb.bot / bb.layer / bb.state).</summary>
public enum BlackboardScope : byte { Bot, Layer, State }

/// <summary>Owns the three blackboard tables in the VM; snapshots and restores them.</summary>
public sealed class Blackboard(ILuaEngine engine)
{
    /// <summary>The engine.</summary>
    public ILuaEngine Engine { get; } = engine;
    /// <summary>JSON snapshot of a scope (layer/state name for those scopes).</summary>
    public string Snapshot(BlackboardScope scope, string? owner = null) => throw new NotImplementedException();
    /// <summary>Set one value from the operator (string parsed as JSON).</summary>
    public void Set(BlackboardScope scope, string? owner, string key, string jsonValue) => throw new NotImplementedException();
    /// <summary>Clear a state's scope (on exit).</summary>
    public void ClearState(string layer) => throw new NotImplementedException();
    /// <summary>Restore the bot scope from a checkpoint.</summary>
    public void Restore(string botScopeJson) => throw new NotImplementedException();
}

/// <summary>One recorded transition.</summary>
public sealed record TransitionRecord(long Tick, DateTime At, string Layer, string From, string To, ConditionKind Kind,
    string Reason, IReadOnlyDictionary<string, double> Values);

/// <summary>Evaluation of one outgoing transition in the last tick (debugger condition panel).</summary>
public sealed record ConditionTrace(string To, ConditionKind Kind, string Expr, double? Value, bool? Result, int HoldElapsedMs);

/// <summary>Live state of one layer for the debugger.</summary>
public sealed record LayerTrace(string Name, int Priority, IReadOnlyList<string> Path, DateTime EnteredAt, long TimeInStateMs,
    double? Progress, DateTime? ProgressBestAt, Resource Claims, IReadOnlyList<ConditionTrace> Conditions);

/// <summary>Everything the debugger shows for one bot at one moment (API_SPEC 7.1).</summary>
public sealed record DebugFrame(string Bot, long Tick, DateTime At, string Version, IReadOnlyList<LayerTrace> Layers,
    IReadOnlyList<TransitionRecord> LastTransitions, string BlackboardJson, IReadOnlyDictionary<string, double> Watches,
    IReadOnlyList<ActionTrace> Actions, IReadOnlyList<ProcedureTrace> Procedures, TickSummary TickStats,
    IReadOnlyList<ErrorTrace> Errors);

/// <summary>Action row for the debugger.</summary>
public sealed record ActionTrace(int Id, ActionKind Kind, ActionState State, ushort Code, string Reason, DateTime SentAt, DateTime? ResolvedAt, string Layer);

/// <summary>Procedure row for the debugger.</summary>
public sealed record ProcedureTrace(ProcedureKind Kind, string Step, DateTime Since);

/// <summary>Tick summary for the debugger.</summary>
public sealed record TickSummary(double P50Us, double P99Us, double AllocP95Bytes, IReadOnlyDictionary<string, double> StepsUs);

/// <summary>A recorded Lua or runtime error.</summary>
public sealed record ErrorTrace(long Tick, string Layer, string State, string Error);

/// <summary>Collects transitions, condition traces, watches and errors; publishes DebugFrames (5 Hz + on transition).</summary>
public sealed class DebugTap
{
    /// <summary>Whether anyone is watching (detailed traces are skipped otherwise).</summary>
    public bool Watched { get; set; }
    /// <summary>Record a transition.</summary>
    public void Transition(TransitionRecord record) => throw new NotImplementedException();
    /// <summary>Record a condition evaluation.</summary>
    public void Condition(string layer, ConditionTrace trace) => throw new NotImplementedException();
    /// <summary>dbg.watch.</summary>
    public void Watch(string name, double value) => throw new NotImplementedException();
    /// <summary>Record an error.</summary>
    public void Error(ErrorTrace error) => throw new NotImplementedException();
    /// <summary>Raised with each published frame (any thread may subscribe; delivered off the tick).</summary>
    public event Action<DebugFrame>? Published;
    /// <summary>Build and publish a frame now.</summary>
    public DebugFrame Publish(BrainInstance brain, AgentContext ctx) => throw new NotImplementedException();
}

/// <summary>Runtime of one state (enter/tick/exit refs, progress tracking).</summary>
public sealed class StateRuntime
{
    /// <summary>Definition.</summary>
    public StateDef Definition { get; } = null!;
    /// <summary>Name.</summary>
    public string Name { get; } = "";
    /// <summary>Entered at (ms).</summary>
    public long EnteredAt { get; internal set; }
    /// <summary>Best progress seen and when.</summary>
    public (double Value, long At) BestProgress { get; internal set; }
    /// <summary>Sub-machine, if any.</summary>
    public MachineRuntime? Sub { get; internal set; }
}

/// <summary>Runtime of a machine: active state, transitions, hold/cooldown timers.</summary>
public sealed class MachineRuntime
{
    /// <summary>Active state.</summary>
    public StateRuntime Active => throw new NotImplementedException();
    /// <summary>Active path root -> leaf.</summary>
    public IReadOnlyList<StateRuntime> Path => throw new NotImplementedException();
    /// <summary>Evaluate transitions along the path; perform the first that fires; returns it or null.</summary>
    public TransitionRecord? EvaluateTransitions(ConditionEvaluator eval, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Tick the leaf state.</summary>
    public LuaCallResult TickLeaf(ILuaEngine engine, long budget) => throw new NotImplementedException();
    /// <summary>Force a state (operator).</summary>
    public void Force(string state, AgentContext ctx) => throw new NotImplementedException();
}

/// <summary>Runtime of a layer: its machine, rate gating, claims.</summary>
public sealed class LayerRuntime
{
    /// <summary>Definition.</summary>
    public LayerDef Definition { get; } = null!;
    /// <summary>Layer index (0 = highest priority).</summary>
    public byte Index { get; }
    /// <summary>Machine.</summary>
    public MachineRuntime Machine { get; } = null!;
    /// <summary>Is the layer due this tick (rate)?</summary>
    public bool Due(long nowMs) => throw new NotImplementedException();
    /// <summary>Resources the active state path claims.</summary>
    public Resource Claims => throw new NotImplementedException();
}

/// <summary>Evaluates conditions: fixed kinds in C#, Lua leaves through the engine, composites with short-circuit.</summary>
public sealed class ConditionEvaluator(ILuaEngine engine, SensorRegistry sensors)
{
    /// <summary>The engine.</summary>
    public ILuaEngine Engine { get; } = engine;
    /// <summary>Sensors for value conditions.</summary>
    public SensorRegistry Sensors { get; } = sensors;
    /// <summary>Evaluate a condition for a state that has been active since <paramref name="enteredAt"/>.</summary>
    public bool Evaluate(ConditionDef condition, AgentContext ctx, long enteredAt, out double? value) => throw new NotImplementedException();
}

/// <summary>Zero-argument numeric readings usable in `value:` conditions and `progress:` (self.hpPct, threat.onUs...).</summary>
public sealed class SensorRegistry
{
    /// <summary>Register a sensor.</summary>
    public void Register(string path, Func<AgentContext, double> read) => throw new NotImplementedException();
    /// <summary>Read a sensor.</summary>
    public double Read(string path, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Known sensor paths (editor pickers).</summary>
    public IReadOnlyList<string> Paths => throw new NotImplementedException();
    /// <summary>The default sensors derived from the API's zero-argument numeric functions.</summary>
    public static SensorRegistry CreateDefault() => throw new NotImplementedException();
}

/// <summary>One bot's brain: engine, layers, blackboard, debug tap. Implements the agent's tick step 6.</summary>
public sealed class BrainInstance : IBrainTicker
{
    /// <summary>Build from a compiled brain version.</summary>
    public BrainInstance(CompiledBrain brain, ILuaEngine engine, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Version hash.</summary>
    public string VersionHash => throw new NotImplementedException();
    /// <summary>Layers, highest priority first.</summary>
    public IReadOnlyList<LayerRuntime> Layers => throw new NotImplementedException();
    /// <summary>Blackboard.</summary>
    public Blackboard Blackboard => throw new NotImplementedException();
    /// <summary>Debug tap.</summary>
    public DebugTap Debug => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Tick(AgentContext ctx) => throw new NotImplementedException();
    /// <inheritdoc/>
    public long LastInstructions => throw new NotImplementedException();
    /// <summary>Hot-swap to a new version at the next tick boundary, keeping matching states and bb.bot.</summary>
    public void Swap(CompiledBrain next, bool keepState) => throw new NotImplementedException();
    /// <summary>Checkpoint: active state paths + bb.bot.</summary>
    public BrainCheckpoint Checkpoint() => throw new NotImplementedException();
    /// <summary>Resume from a checkpoint.</summary>
    public void Resume(BrainCheckpoint checkpoint) => throw new NotImplementedException();
}

/// <summary>A brain version compiled and validated (definition + module sources + manifest version).</summary>
public sealed record CompiledBrain(string Hash, BrainDefinition Definition, IReadOnlyDictionary<string, string> Modules,
    IReadOnlyList<LintResult> Lint);

/// <summary>Saved brain state for resume after relog/redeploy.</summary>
public sealed record BrainCheckpoint(string BotId, DateTime At, string VersionHash,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LayerPaths, string BotBlackboardJson);

/// <summary>Compiles a brain version: parse, resolve modules, lint, hash.</summary>
public static class BrainCompiler
{
    /// <summary>Compile; errors are in the lint results (Severity Error = not applicable).</summary>
    public static CompiledBrain Compile(BrainDefinition definition, IReadOnlyDictionary<string, string> modules, ApiManifest manifest)
        => throw new NotImplementedException();
    /// <summary>Content hash of definition + modules + API version.</summary>
    public static string Hash(BrainDefinition definition, IReadOnlyDictionary<string, string> modules, int apiVersion)
        => throw new NotImplementedException();
}
