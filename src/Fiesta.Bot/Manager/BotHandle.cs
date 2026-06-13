using Fiesta.Bot.Session;

namespace Fiesta.Bot.Manager;

/// <summary>Where a bot is in its lifecycle. Advances monotonically through the
/// login chain to <see cref="InZone"/>, then ends at <see cref="Stopped"/>
/// (clean / kicked) or <see cref="Failed"/> (error before or after zone entry).</summary>
public enum BotPhase
{
    Pending,        // queued, lifecycle task not yet running
    LoggingIn,      // Login → WORLDSELECT_ACK
    SelectingChar,  // WM: LOGINWORLD → (create) → CHAR_LOGIN → tutorial decline
    EnteringZone,   // [1801] MAP_LOGIN_REQ
    InZone,         // session running, heartbeats answered
    Stopping,       // stop requested, winding down
    Stopped,        // ended cleanly (cancelled) or peer closed after zone entry
    Failed,         // errored (see Error)
}

/// <summary>
/// One managed bot: its spawn options, lifecycle phase, the running task, and
/// (once in zone) the live <see cref="BotSession"/>s. Owned by <see cref="BotManager"/>.
/// Phase/character/error are written from the lifecycle task and read from HTTP
/// threads, so they're volatile and the log buffer is locked — this is a status
/// surface, snapshot it with <see cref="Snapshot"/>.
/// </summary>
public sealed class BotHandle
{
    private const int MaxLogLines = 200;
    private readonly List<string> _log = new();
    private readonly object _logGate = new();

    private volatile BotPhase _phase = BotPhase.Pending;
    private volatile string? _charName;
    private volatile string? _error;

    internal BotHandle(string id, BotSpawnOptions options)
    {
        Id = id;
        Options = options;
        CreatedAtUtc = DateTime.UtcNow;
        Cts = new CancellationTokenSource();
    }

    public string Id { get; }
    public BotSpawnOptions Options { get; }
    public DateTime CreatedAtUtc { get; }

    public BotPhase Phase => _phase;
    public string? CharName => _charName;
    public string? Error => _error;

    /// <summary>The in-zone session once entered (null until <see cref="BotPhase.InZone"/>).
    /// The WM session is held open alongside it but isn't the status surface.</summary>
    public BotSession? ZoneSession { get; internal set; }

    /// <summary>The zone perception model (nearby players + chat), live once in zone.</summary>
    public ZoneView? ZoneView { get; internal set; }

    /// <summary>The WM-link session (held open alongside the zone one); needed to
    /// send the WM-side quit on a clean logout.</summary>
    public BotSession? WmSession { get; internal set; }

    internal CancellationTokenSource Cts { get; }
    internal Task? RunTask { get; set; }

    private volatile string? _currentMap;

    /// <summary>The short name of the map the bot is currently on (e.g. "RouN").
    /// Seeded from <see cref="BotSpawnOptions.StartMap"/> at zone entry and updated on
    /// every gate / town-portal transition. Drives which block grid cross-map
    /// navigation pathfinds over; null if the start map wasn't supplied and no
    /// transition has happened yet.</summary>
    public string? CurrentMap => _currentMap;
    internal void SetCurrentMap(string map) => _currentMap = map;

    private readonly object _posGate = new();
    private (uint X, uint Y)? _pos;

    /// <summary>The bot's best-known world position: seeded from the zone-login spawn
    /// coord and advanced as it issues move commands. Null until in zone (or if the
    /// spawn coord wasn't captured). Lets navigation default the "from" point.</summary>
    public (uint X, uint Y)? Position { get { lock (_posGate) return _pos; } }

    internal void SetPosition(uint x, uint y) { lock (_posGate) _pos = (x, y); }

    private volatile object? _selfHandleBox; // ushort? boxed (volatile needs reference)

    /// <summary>The bot's own in-zone character handle (from the [1802] login ack).
    /// Needed to self-target — e.g. cast a heal on yourself rather than your current
    /// (enemy) target. Null until in zone.</summary>
    public ushort? SelfHandle => _selfHandleBox as ushort?;
    internal void SetSelfHandle(ushort handle) => _selfHandleBox = handle;

    /// <summary>The skill id of the bot's most recent cast attempt. Set by
    /// <see cref="Manager.BotManager.CastAsync"/> and
    /// <see cref="Manager.BotManager.CastGroundAsync"/> before sending, so the
    /// cast-fail reactive layer (subscribed to <see cref="ZoneView.CastFailed"/>)
    /// can retry with the same skill after approaching or recharging. 0 = none.</summary>
    internal volatile ushort LastCastSkill;

    /// <summary>The target handle (or 0 for ground-cast) of the bot's most recent cast
    /// attempt. Updated alongside <see cref="LastCastSkill"/>.</summary>
    internal volatile ushort LastCastTarget;

    /// <summary>Cancellation for the currently-running <see cref="Manager.BotManager.WalkPath"/>,
    /// if any — cancelled to abort a walk early (e.g. on a server MOVEFAIL so the bot
    /// stops banging into an off-grid obstacle). Set/cleared by the walk task.</summary>
    internal CancellationTokenSource? WalkCts { get; set; }

    /// <summary>Cancellation for the currently-running follow loop (chase a target
    /// player), if any. Cancelled to stop following — and replaced when a new follow
    /// starts. Follow is client-side (target + streamed moves), so it lives here.</summary>
    internal CancellationTokenSource? FollowCts { get; set; }

    /// <summary>Cancellation for the currently-running autonomous travel (multi-map
    /// <see cref="Manager.BotManager.TravelTo"/>) loop, if any. Cancelled to abort the
    /// journey; replaced when a new travel starts.</summary>
    internal CancellationTokenSource? TravelCts { get; set; }

    /// <summary>The bot's current walk speed in world-units per second, driven by
    /// MOVESPEED broadcasts (0x203E / 0xCC0D). Defaults to 120.0. The navigation
    /// layer paces movement packets against this — a mount or speed buff updates it
    /// live so the bot never sends steps too fast for its current speed.</summary>
    public double WalkSpeed { get; set; } = 120.0;

    /// <summary>The map name the bot is *intentionally* travelling into (set by the
    /// travel loop right before it takes a gate). The handoff packet carries only the
    /// destination map *id*, so on the first visit the catalog can't name it — this lets
    /// <see cref="Manager.BotManager.OnMapChanged"/> resolve the real short-name (and
    /// learn id↔name) instead of falling back to a synthetic "map#&lt;id&gt;" label.
    /// Null when not travelling (a manual gate / town portal just uses the fallback).</summary>
    internal volatile string? PendingDestMap;

    private int _mapChangeSeq;

    /// <summary>Monotonic counter bumped once per map transition (gate / town portal,
    /// in-band or cross-server). The travel loop snapshots it before taking a gate and
    /// waits for it to advance — a transition-agnostic "did the warp land?" signal that
    /// survives the cross-server reconnect (which swaps the ZoneView out).</summary>
    public int MapChangeSeq => Volatile.Read(ref _mapChangeSeq);
    internal void BumpMapChange() => Interlocked.Increment(ref _mapChangeSeq);

    internal void SetPhase(BotPhase phase) => _phase = phase;
    internal void SetCharName(string name) => _charName = name;
    internal void SetError(string error) => _error = error;

    internal void Log(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {message}";
        lock (_logGate)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
    }

    private IReadOnlyList<string> RecentLog()
    {
        lock (_logGate) return _log.ToArray();
    }

    /// <summary>A consistent, serializable point-in-time view for the API.</summary>
    public BotSnapshot Snapshot()
    {
        var state = ZoneSession?.State;
        var view = ZoneView;
        return new BotSnapshot(
            Id: Id,
            Phase: Phase.ToString(),
            Host: Options.Host,
            Username: Options.Credentials.Username,
            Character: CharName,
            Connected: state?.Connected ?? false,
            InboundFrames: state?.InboundCount ?? 0,
            Heartbeats: state?.HeartbeatCount ?? 0,
            LastOpcode: state is { } s ? $"0x{s.LastOpcode:X4}" : null,
            UptimeSeconds: state is { } u ? Math.Round(u.Uptime.TotalSeconds, 1) : 0,
            DisconnectReason: state?.DisconnectReason,
            Error: Error,
            NearbyPlayers: view?.NearbyCount ?? 0,
            LastChat: view?.LastChat is { } c ? $"<{c.SenderName ?? $"h{c.Handle}"}> {c.Text}" : null,
            Position: Position is { } p ? $"{p.X},{p.Y}" : null,
            Map: CurrentMap,
            Mounted: view?.IsMounted ?? false,
            CreatedAtUtc: CreatedAtUtc,
            RecentLog: RecentLog());
    }
}

/// <summary>Serializable point-in-time view of a bot, returned by the control API.</summary>
public sealed record BotSnapshot(
    string Id,
    string Phase,
    string Host,
    string Username,
    string? Character,
    bool Connected,
    long InboundFrames,
    long Heartbeats,
    string? LastOpcode,
    double UptimeSeconds,
    string? DisconnectReason,
    string? Error,
    int NearbyPlayers,
    string? LastChat,
    string? Position,
    string? Map,
    bool Mounted,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> RecentLog);
