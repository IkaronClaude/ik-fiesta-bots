using Fiesta.Bot.Agent;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Net;

namespace Fiesta.Bot.Fleet;

/// <summary>Lifecycle states of a supervised bot.</summary>
public enum SupervisorState : byte { Offline, LoggingIn, World, InZone, Handoff, Paused, Stopping, Reconnecting, Failed }

/// <summary>Persisted configuration of one bot.</summary>
public sealed record BotRecord(string Id, string Username, string PasswordMd5, string Character, Endpoint Login,
    string? Brain, string? Channel, string? VersionHash, bool Autostart, bool PacketLog);

/// <summary>Summary row for the fleet page.</summary>
public sealed record BotSummary(string Id, SupervisorState State, string? Map, int Level, IReadOnlyDictionary<string, string> LayerStates,
    string? VersionHash, double TickP99Us, double ExpPerHour, double DeathsPerHour, string? LastError, string? Health);

/// <summary>Owns every bot's lifecycle: spawn, login, handoff, reconnect, checkpoint/resume, stop. The only writer of
/// bot lifecycle state; operator commands reach the agent through its control queue.</summary>
public interface IBotSupervisor
{
    /// <summary>All bots.</summary>
    IReadOnlyList<BotSummary> List();
    /// <summary>Create (and optionally start) a bot.</summary>
    Task<BotSummary> SpawnAsync(BotRecord record, CancellationToken ct);
    /// <summary>Log in and start the brain.</summary>
    Task StartAsync(string id, CancellationToken ct);
    /// <summary>Stop the brain and log out cleanly (flee first if in combat).</summary>
    Task StopAsync(string id, bool logout, CancellationToken ct);
    /// <summary>Checkpoint, log out, log in, resume.</summary>
    Task RelogAsync(string id, CancellationToken ct);
    /// <summary>Pause / resume the brain (link kept).</summary>
    void SetPaused(string id, bool paused);
    /// <summary>Post an operator command to the bot's tick.</summary>
    void Post(string id, ControlCommand command);
    /// <summary>Remove a bot (stops it first).</summary>
    Task ForgetAsync(string id, CancellationToken ct);
    /// <summary>The agent of a running bot.</summary>
    BotAgent? Agent(string id);
    /// <summary>The brain of a running bot.</summary>
    BrainInstance? Brain(string id);
    /// <summary>Drain for a deploy: checkpoint and log out every bot.</summary>
    Task DrainAsync(CancellationToken ct);
}

/// <summary>Default supervisor.</summary>
public sealed class BotSupervisor : IBotSupervisor
{
    /// <summary>Create with its collaborators.</summary>
    public BotSupervisor(IFleetStore store, IBrainRepository brains, TickScheduler scheduler, ICheckpointStore checkpoints)
        => throw new NotImplementedException();
    /// <inheritdoc/>
    public IReadOnlyList<BotSummary> List() => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task<BotSummary> SpawnAsync(BotRecord record, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task StartAsync(string id, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task StopAsync(string id, bool logout, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task RelogAsync(string id, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void SetPaused(string id, bool paused) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Post(string id, ControlCommand command) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task ForgetAsync(string id, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public BotAgent? Agent(string id) => throw new NotImplementedException();
    /// <inheritdoc/>
    public BrainInstance? Brain(string id) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task DrainAsync(CancellationToken ct) => throw new NotImplementedException();
}

/// <summary>K pinned tick workers; each owns a fixed partition of agents and ticks them at their rate.</summary>
public sealed class TickScheduler : IDisposable
{
    /// <summary>Create with a worker count (default cores - 1) and base tick interval.</summary>
    public TickScheduler(int workers, TimeSpan interval) => throw new NotImplementedException();
    /// <summary>Add an agent to the least loaded worker.</summary>
    public void Add(BotAgent agent) => throw new NotImplementedException();
    /// <summary>Remove an agent (takes effect between ticks).</summary>
    public void Remove(BotAgent agent) => throw new NotImplementedException();
    /// <summary>Start the workers.</summary>
    public void Start() => throw new NotImplementedException();
    /// <summary>Per-worker load (agents, busy fraction).</summary>
    public IReadOnlyList<(int Agents, double Busy)> Load => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Dispose() => throw new NotImplementedException();
}

/// <summary>Information about one brain version.</summary>
public sealed record BrainVersionInfo(string Hash, string Brain, int Api, DateTime Created, string Author, string Message,
    IReadOnlyList<string> Modules);

/// <summary>A named pointer to a brain version.</summary>
public sealed record ChannelInfo(string Brain, string Channel, string Hash);

/// <summary>One apply of a brain version to a bot.</summary>
public sealed record AssignmentRecord(string Bot, DateTime At, string? FromHash, string ToHash, string By, string Reason, bool KeptState);

/// <summary>Diff between two brain versions.</summary>
public sealed record BrainDiff(string From, string To, string DefinitionDiff, IReadOnlyList<(string Path, string UnifiedDiff)> Modules);

/// <summary>Content-addressed store of brain and library versions, channels and assignments; mirrored to the private git repo.</summary>
public interface IBrainRepository
{
    /// <summary>Brain names with their latest version and channels.</summary>
    IReadOnlyList<(string Brain, string Latest, IReadOnlyList<ChannelInfo> Channels)> Brains();
    /// <summary>Versions of a brain, newest first.</summary>
    IReadOnlyList<BrainVersionInfo> Versions(string brain);
    /// <summary>Load and compile a version.</summary>
    CompiledBrain Load(string hash);
    /// <summary>Create a version (lint first); returns it or the lint errors.</summary>
    (BrainVersionInfo? Version, IReadOnlyList<LintResult> Lint) Create(string brain, BrainDefinition definition,
        IReadOnlyDictionary<string, string> modules, string author, string message);
    /// <summary>Diff two versions.</summary>
    BrainDiff Diff(string fromHash, string toHash);
    /// <summary>Move a channel.</summary>
    void SetChannel(string brain, string channel, string hash);
    /// <summary>Resolve a channel to a hash.</summary>
    string? ResolveChannel(string brain, string channel);
    /// <summary>Library modules with their latest version and users.</summary>
    IReadOnlyList<(string Path, string Latest, IReadOnlyList<string> UsedBy)> Libraries();
    /// <summary>Create a library module version.</summary>
    string CreateLibraryVersion(string path, string source, string author, string message);
    /// <summary>Record an assignment.</summary>
    void RecordAssignment(AssignmentRecord record);
    /// <summary>Assignment history of a bot, newest first.</summary>
    IReadOnlyList<AssignmentRecord> Assignments(string bot);
}

/// <summary>Persistent fleet configuration (Postgres or SQLite).</summary>
public interface IFleetStore
{
    /// <summary>All bot records.</summary>
    IReadOnlyList<BotRecord> All();
    /// <summary>Insert or update.</summary>
    void Upsert(BotRecord record);
    /// <summary>Delete.</summary>
    bool Delete(string id);
}

/// <summary>Brain checkpoints for resume.</summary>
public interface ICheckpointStore
{
    /// <summary>Save.</summary>
    void Save(BrainCheckpoint checkpoint);
    /// <summary>Latest for a bot.</summary>
    BrainCheckpoint? Latest(string botId);
}

/// <summary>Health problems the monitor raises (REDESIGN 10).</summary>
public enum HealthKind : byte { NoTick, TickOverBudget, StateOverBudget, NoProgress, TransitionCycle, Disconnected, GcPause }

/// <summary>One alert.</summary>
public sealed record HealthAlert(string Bot, HealthKind Kind, DateTime At, string Text, string? Layer, string? State);

/// <summary>Watches agents and brains for the health rules; alerts go to the log (CRIT), the UI and metrics.</summary>
public sealed class HealthMonitor
{
    /// <summary>Evaluate all bots (called once a second off the tick).</summary>
    public IReadOnlyList<HealthAlert> Evaluate(IBotSupervisor supervisor) => throw new NotImplementedException();
    /// <summary>Raised per new alert.</summary>
    public event Action<HealthAlert>? Alert;
}

/// <summary>Records a bot's inbound events, control commands, clock and job results to a .botrec file.</summary>
public sealed class SessionRecorder : IDisposable
{
    /// <summary>Start recording to a file.</summary>
    public SessionRecorder(string path, string botId, string versionHash) => throw new NotImplementedException();
    /// <summary>Record an inbound event.</summary>
    public void Inbound(in Fiesta.Bot.Protocol.InboundEvent evt) => throw new NotImplementedException();
    /// <summary>Record a tick boundary with its clock.</summary>
    public void TickBoundary(long tick, long nowMs) => throw new NotImplementedException();
    /// <summary>Record a control command.</summary>
    public void Control(ControlCommand command) => throw new NotImplementedException();
    /// <inheritdoc/>
    public void Dispose() => throw new NotImplementedException();
}

/// <summary>Replays a .botrec through an agent with a recorded clock; reports intents and transitions for comparison.</summary>
public sealed class SessionReplayer
{
    /// <summary>Open a recording.</summary>
    public SessionReplayer(string path) => throw new NotImplementedException();
    /// <summary>Run to the end (or a tick) and return what the agent emitted.</summary>
    public ReplayResult Run(BotAgent agent, long untilTick = long.MaxValue) => throw new NotImplementedException();
}

/// <summary>What a replay produced.</summary>
public sealed record ReplayResult(long Ticks, IReadOnlyList<(long Tick, ushort Opcode)> Intents, IReadOnlyList<TransitionRecord> Transitions,
    double TickP99Us, double AllocP95Bytes);
