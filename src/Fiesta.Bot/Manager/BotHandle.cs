using System.Linq;
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
    // Big enough that a verbose firehose (move/cast every tick) doesn't evict the headline
    // Note/Info lines before a tailer reads them. Filtered down per-level on the way out.
    private const int MaxLogLines = 1500;
    private readonly List<(BotLogLevel Level, string Line)> _log = new();
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

    private volatile uint _level;

    /// <summary>The character's level, as the bot received it over the wire in the WM
    /// avatar list (<c>LOGINWORLD_ACK</c>) at char-select — the authoritative source, not
    /// inferred from HP. 0 until selected.</summary>
    public uint Level => _level;
    internal void SetLevel(ushort level) => _level = level;

    private volatile int _class;
    /// <summary>The character's ClassName.shn ClassID (1=Fighter, 6=Cleric, 11=Archer,
    /// 16=Mage, 21=Joker, 26=Sentinel; promotions in between), from the WM avatar shape at
    /// char-select. 0 until selected. Used to pick the class-appropriate quest reward.</summary>
    public int Class => _class;
    internal void SetClass(byte cls) => _class = cls;

    /// <summary>The in-zone session once entered (null until <see cref="BotPhase.InZone"/>).
    /// The WM session is held open alongside it but isn't the status surface.</summary>
    public BotSession? ZoneSession { get; internal set; }

    /// <summary>The zone perception model (nearby players + chat), live once in zone.</summary>
    public ZoneView? ZoneView { get; internal set; }

    /// <summary>The WM-link session (held open alongside the zone one); needed to
    /// send the WM-side quit on a clean logout.</summary>
    public BotSession? WmSession { get; internal set; }

    /// <summary>Active packet log (both directions, plaintext) when enabled via the
    /// /packetlog endpoint, else null. Stored on the handle so it can be re-attached to
    /// the zone session after a cross-server handoff swaps it out.</summary>
    internal Net.PacketLog? PacketLog { get; set; }

    /// <summary>Name of the player whose party invite (NC_PARTY_JOINPROPOSE_REQ, 0x3803)
    /// is currently pending and unanswered, or null if none. Tracked off the WM link so
    /// the bot can accept without being told the inviter's name (an unanswered invite
    /// leaves the party state stuck — accept or decline clears it). Cleared on join/leave.</summary>
    public string? PendingPartyInviter { get; set; }

    /// <summary>Name of the player whose incoming friend request (NC_FRIEND_SET_CONFIRM_REQ,
    /// 0x5403) is pending and unanswered, or null if none. Tracked off the WM link so the bot
    /// can auto-confirm (friendConfirm) without being told the requester's name — lets an
    /// operator friend the bot and have it accept on its own. Cleared once answered.</summary>
    public string? PendingFriendRequester { get; set; }

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

    /// <summary>The Lua behaviour script currently looping on this bot, if any. Set by
    /// <see cref="Manager.BotManager.ApplyScript"/>; torn down on stop / replace. The
    /// runner subscribes to <see cref="Events"/> so it survives ZoneView swaps.</summary>
    internal Scripting.BotScriptRunner? ScriptRunner { get; set; }

    /// <summary>Behaviour graphs (state machines) running CONCURRENTLY on this bot, keyed by
    /// graph name. Each is independent (own thread/VM/state), so e.g. a "join_requests" WM
    /// graph runs alongside a "gameplay" graph without interfering. Applying a graph replaces
    /// only the same-named one.</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, Scripting.BehaviorGraphRunner> GraphRunners { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable per-bot event stream. Unlike <see cref="ZoneView"/> (swapped out
    /// on a cross-server reconnect), this hub lives for the bot's whole life, so a
    /// script's subscriptions don't drop across map handoffs. The manager forwards the
    /// ZoneView/session events here; a future WS <c>/events</c> endpoint reuses it.
    /// Handlers MUST NOT block — enqueue and return (raised on the session read loop).</summary>
    public event Action<BotEvent>? Events;

    /// <summary>Forward an event to the hub. Swallows handler exceptions so a bad
    /// subscriber can never kill the read loop that raised it.</summary>
    internal void Emit(BotEvent e)
    {
        try { Events?.Invoke(e); } catch { /* a subscriber threw — never break the loop */ }
    }

    internal void SetPhase(BotPhase phase) => _phase = phase;
    internal void SetCharName(string name) => _charName = name;
    internal void SetError(string error) => _error = error;

    /// <summary>Raised once per appended log line (the same timestamped text the ring
    /// buffer holds). The live log-stream endpoint subscribes to tail a bot in real
    /// time. Raised from whatever thread logged — handlers must not block.</summary>
    public event Action<string>? LogLine;

    internal void Log(string message) => Log(BotLogLevel.Note, message);

    /// <summary>Append a log line at the given verbosity. The level is stamped into the
    /// text (<c>N</c>/<c>I</c>/<c>V</c> after the timestamp) so a raw tail is still readable,
    /// and retained structurally so the snapshot/tail endpoints can filter by level.</summary>
    internal void Log(BotLogLevel level, string message)
    {
        var tag = level switch { BotLogLevel.Verbose => "V", BotLogLevel.Info => "I", _ => "N" };
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {tag} {message}";
        lock (_logGate)
        {
            _log.Add((level, line));
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        // Fan out to live tailers outside the lock; never let a subscriber break logging.
        try { LogLine?.Invoke(line); } catch { }
    }

    // The polled snapshot stays readable: headline + progress only (drop the verbose firehose).
    // Pull the full stream (incl. Verbose) from the /log tail endpoint when chasing a bug.
    private IReadOnlyList<string> RecentLog()
    {
        lock (_logGate)
            return _log.Where(e => e.Level <= BotLogLevel.Info).Select(e => e.Line).ToArray();
    }

    /// <summary>The most recent <paramref name="max"/> log lines at or quieter than
    /// <paramref name="maxLevel"/> (Note ⊂ Info ⊂ Verbose). <paramref name="max"/> ≤ 0 or
    /// past the buffer returns all matching lines — the backfill a tail connection replays.</summary>
    public IReadOnlyList<string> RecentLines(int max, BotLogLevel maxLevel = BotLogLevel.Verbose)
    {
        lock (_logGate)
        {
            var filtered = _log.Where(e => e.Level <= maxLevel).Select(e => e.Line).ToList();
            if (max <= 0 || max >= filtered.Count) return filtered;
            return filtered.GetRange(filtered.Count - max, max);
        }
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
            Level: _level == 0 ? null : _level,
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
            Hp: view?.Hp,
            Sp: view?.Sp,
            MaxHp: view is { MaxHp: > 0 } ? view.MaxHp : null,
            MaxSp: view is { MaxSp: > 0 } ? view.MaxSp : null,
            HpStones: view?.HpStones,
            SpStones: view?.SpStones,
            InCombat: view?.InCombat ?? false,
            Dead: view?.Dead ?? false,
            Drops: view?.Drops.Count ?? 0,
            Script: GraphRunners.IsEmpty
                ? ScriptRunner?.StatusLine
                : string.Join(" | ", GraphRunners.Values.Select(g => g.StatusLine)),
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
    uint? Level,
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
    uint? Hp,
    uint? Sp,
    uint? MaxHp,
    uint? MaxSp,
    int? HpStones,
    int? SpStones,
    bool InCombat,
    bool Dead,
    int Drops,
    string? Script,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> RecentLog);
