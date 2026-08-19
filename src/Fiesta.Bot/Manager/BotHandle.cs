using System.Linq;
using Fiesta.Bot.Session;

namespace Fiesta.Bot.Manager;

/// <summary>Where a bot is in its lifecycle</summary>
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

/// <summary>One managed bot: its spawn options, lifecycle phase, the running task, and (once in zone) the live s</summary>
public sealed class BotHandle
{
    private const int MaxLogLines = 100_000;
    private const int MaxLogChars = 6_000_000;          // ~14MB per bot once UTF-16 + object headers
    private const int TrimChunk = 8_192;
    private readonly List<(BotLogLevel Level, string Line)> _log = new();
    private long _logChars;
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

    public DateTime LastBashSentUtc { get; internal set; } = DateTime.MinValue;

    /// <summary>The handle we have already told the server we are targeting</summary>
    public ushort CurrentTarget { get; internal set; }

    /// <summary>True while we believe the SERVER still holds as our target</summary>
    public bool TargetAsserted { get; internal set; }

    /// <summary>When was last (re-)asserted to the server</summary>
    public DateTime TargetSetAtUtc { get; internal set; } = DateTime.MinValue;

    // STRUCTURED EVENT STREAM ────────────────────────────────────────────────────────────────── Operator 2026-08-12…
    public sealed record BotEventRec(DateTime AtUtc, string Kind, string Detail);

    private readonly List<BotEventRec> _events = new();
    private const int MaxEvents = 20_000;

    public void NoteEvent(string kind, string detail = "")
    {
        lock (_events)
        {
            _events.Add(new BotEventRec(DateTime.UtcNow, kind, detail));
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    /// <summary>Events, oldest first, optionally filtered by kind</summary>
    public IReadOnlyList<BotEventRec> EventLog(string? kind = null)
    {
        lock (_events)
            return kind is null ? _events.ToArray()
                 : _events.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>The server has (or may have) dropped our target — re-assert before the next attack</summary>
    public void InvalidateTarget(string why)
    {
        if (!TargetAsserted) return;
        TargetAsserted = false;
        Log(BotLogLevel.Verbose, $"[target] invalidated ({why}) — will re-send TARGETTING before the next attack");
    }

    public DateTime ZoneEnteredUtc { get; internal set; } = DateTime.MinValue;

    public DateTime LastRelogUtc { get; internal set; } = DateTime.MinValue;
    public int ShortSessionStreak { get; internal set; }

    // WHERE THE HOURS ACTUALLY GO ────────────────────────────────────────────────────────────── Operator 2026-08-12…
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _phaseSeconds = new();
    private string? _currentPhase;
    private DateTime _phaseSinceUtc = DateTime.UtcNow;
    private readonly object _phaseGate = new();

    /// <summary>Seconds spent in each driver phase since this bot started, newest total wins</summary>
    public IReadOnlyDictionary<string, double> PhaseSeconds => _phaseSeconds;

    /// <summary>The phase the driver says it is in, and how long it has been there</summary>
    public (string? Phase, double Seconds) CurrentPhase
    {
        get { lock (_phaseGate) return (_currentPhase, (DateTime.UtcNow - _phaseSinceUtc).TotalSeconds); }
    }

    /// <summary>Called by the driver whenever it sets its phase</summary>
    public sealed record PhaseVisit(string Phase, DateTime StartedUtc, double Seconds);

    private readonly List<PhaseVisit> _phaseLog = new();
    private const int MaxPhaseVisits = 5000;   // ~days of transitions; bounded so it cannot grow forever

    /// <summary>Every phase visit, oldest first, including the one currently open (its Seconds is live)</summary>
    public IReadOnlyList<PhaseVisit> PhaseLog
    {
        get
        {
            lock (_phaseGate)
            {
                var outp = new List<PhaseVisit>(_phaseLog);
                if (_currentPhase is { } cur)
                    outp.Add(new PhaseVisit(cur, _phaseSinceUtc, (DateTime.UtcNow - _phaseSinceUtc).TotalSeconds));
                return outp;
            }
        }
    }

    public void NotePhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return;
        bool flush = false;
        lock (_phaseGate)
        {
            var now = DateTime.UtcNow;
            if (_currentPhase is { } prev)
            {
                var d = (now - _phaseSinceUtc).TotalSeconds;
                if (d > 0 && d < 3600) _phaseSeconds.AddOrUpdate(prev, d, (_, v) => v + d);
                // Record the VISIT only when the phase actually changes — NotePhase is called every tick to keep the open phase'…
                if (!string.Equals(prev, phase, StringComparison.Ordinal))
                {
                    _phaseLog.Add(new PhaseVisit(prev, _phaseSinceUtc, d));
                    NoteEvent("phase", $"{prev} -> {phase} after {d:F1}s");
                    if (_phaseLog.Count > MaxPhaseVisits) _phaseLog.RemoveRange(0, _phaseLog.Count - MaxPhaseVisits);
                }
            }
            if (!string.Equals(_currentPhase, phase, StringComparison.Ordinal)) _phaseSinceUtc = now;
            _currentPhase = phase;
            // Flush at most every 30s: frequent enough that a pod kill loses seconds, not hours, and rare enough that a per-…
            if ((now - _phaseFlushUtc).TotalSeconds >= 30) { _phaseFlushUtc = now; flush = true; }
        }
        if (flush) PhasePersist?.Invoke(Id, PhaseSeconds);
    }

    private DateTime _phaseFlushUtc = DateTime.MinValue;

    /// <summary>Set by the manager so phase totals reach durable storage — see the note on NpcKnowledge.SavePhaseSeconds for w…</summary>
    public Action<string, IReadOnlyDictionary<string, double>>? PhasePersist { get; set; }

    /// <summary>Carry forward totals from earlier runs of this same bot</summary>
    internal BotLogFile? LogFile { get; set; }

    /// <summary>Load history from BEFORE this process started into the in-memory ring, so /log and the snapshot endpoints show…</summary>
    internal void SeedLogFromDisk(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        lock (_logGate)
        {
            var restored = new List<(BotLogLevel Level, string Line)>(lines.Count + 1);
            foreach (var l in lines)
            {
                // "HH:mm:ss.fff …" — recover the level so ?level= filtering still works on history
                var lvl = BotLogLevel.Note;
                var t = l.Length > 13 ? l[13] : 'N';
                if (t == 'V') lvl = BotLogLevel.Verbose; else if (t == 'I') lvl = BotLogLevel.Info;
                restored.Add((lvl, l));
            }
            restored.Add((BotLogLevel.Note, $"{DateTime.UtcNow:HH:mm:ss.fff} N ── {lines.Count} line(s) above restored from disk (previous session) ──"));
            _log.InsertRange(0, restored);
            foreach (var e in restored) _logChars += e.Line.Length;
            TrimLogLocked();
        }
    }

    public void SeedPhaseSeconds(IReadOnlyDictionary<string, double> prior)
    {
        foreach (var (k, v) in prior) if (v > 0) _phaseSeconds.AddOrUpdate(k, v, (_, old) => old + v);
    }

    public bool LastDialogConcluded { get; internal set; }

    /// <summary>The last-applied Lua script (name/source/tick) — kept so a self-relog (bot.relog / stuck instance recovery) ca…</summary>
    public string? LastScriptName { get; internal set; }
    public string? LastScriptSource { get; internal set; }
    public int LastScriptTickMs { get; internal set; }

    public BotPhase Phase => _phase;
    public string? CharName => _charName;
    public string? Error => _error;

    private volatile uint _level;

    /// <summary>The character's level, as the bot received it over the wire in the WM avatar list ( LOGINWORLD_ACK ) at char-s…</summary>
    public uint Level => _level;
    internal void SetLevel(ushort level) => _level = level;

    private volatile int _class;
    /// <summary>The character's ClassName.shn ClassID (1=Fighter, 6=Cleric, 11=Archer, 16=Mage, 21=Joker, 26=Sentinel; promoti…</summary>
    public int Class => _class;
    internal void SetClass(byte cls) => _class = cls;

    /// <summary>The in-zone session once entered (null until )</summary>
    public BotSession? ZoneSession { get; internal set; }

    /// <summary>The zone perception model (nearby players + chat), live once in zone</summary>
    public ZoneView? ZoneView { get; internal set; }

    /// <summary>The WM-link session (held open alongside the zone one); needed to send the WM-side quit on a clean logout</summary>
    public BotSession? WmSession { get; internal set; }

    /// <summary>Active packet log (both directions, plaintext) when enabled via the /packetlog endpoint, else null</summary>
    internal Net.PacketLog? PacketLog { get; set; }

    /// <summary>ALWAYS-ON bounded capture of the last 100 frames, both directions</summary>
    internal Net.PacketRing PacketRing { get; } = new(100);

    /// <summary>The tap to install on every session: feeds the always-on ring, and the file log too when one is enabled</summary>
    internal Action<bool, ushort, ReadOnlyMemory<byte>> CombinedTap =>
        (outbound, opcode, payload) =>
        {
            PacketRing.Tap(outbound, opcode, payload);
            PacketLog?.Tap(outbound, opcode, payload);
        };

    /// <summary>Name of the player whose party invite (NC_PARTY_JOINPROPOSE_REQ, 0x3803) is currently pending and unanswered,…</summary>
    public string? PendingPartyInviter { get; set; }

    /// <summary>Name of the player whose incoming friend request (NC_FRIEND_SET_CONFIRM_REQ, 0x5403) is pending and unanswered…</summary>
    public string? PendingFriendRequester { get; set; }

    /// <summary>Live party roster, keyed by member char-name — the FOUNDATION for TEAMWORK coordination (cleric team-heal read…</summary>
    public sealed class PartyMember
    {
        public string Name = "";
        public byte ChrClass, Level;
        public uint Hp, MaxHp, Sp, MaxSp;
        public uint X, Y;
    }
    public System.Collections.Concurrent.ConcurrentDictionary<string, PartyMember> PartyMembers { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    internal CancellationTokenSource Cts { get; }
    internal Task? RunTask { get; set; }

    private volatile string? _currentMap;

    /// <summary>The short name of the map the bot is currently on</summary>
    public string? CurrentMap => _currentMap;
    internal void SetCurrentMap(string map) => SetCurrentMap(map, null, "legacy");

    /// <summary>The map id the SERVER last gave us, and how the NAME was resolved from it. A wrong map identity
    /// is invisible today: only the resolved name is exposed, so a name/id disagreement cannot be seen without
    /// rendering the .shbd by hand. Measured 2026-08-19: a death + revive-in-place left the bot naming itself
    /// EldCem01 while standing on EldGbl02, producing ~2,500 MOVEFAILs/hour for nine hours.</summary>
    public int? CurrentMapId { get; private set; }
    public string? CurrentMapSource { get; private set; }

    internal void SetCurrentMap(string map, int? mapId, string source)
    {
        _currentMap = map;
        if (mapId is { } id) CurrentMapId = id;
        CurrentMapSource = source;
    }

    private readonly object _posGate = new();
    private (uint X, uint Y)? _pos;

    private double _segFromX, _segFromY, _segToX, _segToY;
    private DateTime _segStartUtc;
    private double _segDurationMs;   // 0 = standing still; _pos is then the whole truth

    /// <summary>The bot's best-known world position, INTERPOLATED along the move currently in flight</summary>
    public (uint X, uint Y)? Position { get { lock (_posGate) return InterpolatedLocked(); } }

    /// <summary>Current point along the in-flight segment</summary>
    private (uint X, uint Y)? InterpolatedLocked()
    {
        if (_pos is not { } p) return null;
        if (_segDurationMs <= 0) return p;
        var f = Math.Clamp((DateTime.UtcNow - _segStartUtc).TotalMilliseconds / _segDurationMs, 0.0, 1.0);
        return ((uint)Math.Round(_segFromX + (_segToX - _segFromX) * f),
                (uint)Math.Round(_segFromY + (_segToY - _segFromY) * f));
    }

    /// <summary>Begin a move to (toX,toY) at , and return the position the move actually STARTS from — the interpolated point…</summary>
    internal (uint X, uint Y) BeginMove(uint toX, uint toY, double unitsPerSec)
    {
        (uint X, uint Y) cur; (uint X, uint Y)? prev;
        lock (_posGate)
        {
            prev = _pos;
            cur = InterpolatedLocked() ?? (toX, toY);
            var dist = Math.Sqrt(Math.Pow(toX - (double)cur.X, 2) + Math.Pow(toY - (double)cur.Y, 2));
            _segFromX = cur.X; _segFromY = cur.Y; _segToX = toX; _segToY = toY;
            _segStartUtc = DateTime.UtcNow;
            _segDurationMs = (unitsPerSec > 0 && dist > 0) ? dist / unitsPerSec * 1000.0 : 0;
            _pos = cur;   // anchor the segment's origin
        }
        SetFacing(cur.X, cur.Y, toX, toY);   // moving A->B IS what turns us to face B
        NoteMoved(prev, cur);
        return cur;
    }

    internal void SetPosition(uint x, uint y)
    {
        (uint X, uint Y)? prev;
        // An explicit set (MOVEFAIL snap, zone entry, map handoff) is authoritative: it ends any in-flight segment, so w…
        lock (_posGate) { prev = InterpolatedLocked(); _pos = (x, y); _segDurationMs = 0; }
        NoteMoved(prev, (x, y));
    }

    private void NoteMoved((uint X, uint Y)? prev, (uint X, uint Y) cur)
    {
        var x = cur.X; var y = cur.Y;
        // Every position update flows through here — walk steps, MOVEFAIL resyncs, map handoffs — so it is the one place…
        Trace.Sample(CurrentMap, (int)x, (int)y);
        if (prev is { } p && !string.IsNullOrEmpty(CurrentMap) && string.Equals(CurrentMap, _lastTraceMap, StringComparison.Ordinal))
        {
            var d = Math.Sqrt(Math.Pow((double)x - p.X, 2) + Math.Pow((double)y - p.Y, 2));
            if (d > 0 && d < 2000) Metrics.LogMetric("distance", d);
        }
        _lastTraceMap = CurrentMap;
    }
    private string? _lastTraceMap;

    /// <summary>The target of the most recently issued MOVERUN step (the tile the bot was trying to enter)</summary>
    public (uint X, uint Y)? LastMoveTarget { get; internal set; }

    /// <summary>MOVEFAIL-streak tracking for the perpendicular-to-wall UNSTICK (operator 2026-07-13): the snap-back position of the last MOVEFAIL and how many in a row landed at ~the same spot</summary>
    public (uint X, uint Y)? LastMoveFailPos { get; internal set; }
    public int MoveFailStreak { get; internal set; }
    public DateTime LastUnstickUtc { get; internal set; }

    private volatile object? _selfHandleBox; // ushort? boxed (volatile needs reference)

    /// <summary>The bot's own in-zone character handle (from the [1802] login ack)</summary>
    public ushort? SelfHandle => _selfHandleBox as ushort?;
    internal void SetSelfHandle(ushort handle) => _selfHandleBox = handle;

    /// <summary>The skill id of the bot's most recent cast attempt</summary>
    internal volatile ushort LastCastSkill;

    /// <summary>The target handle (or 0 for ground-cast) of the bot's most recent cast attempt</summary>
    internal volatile ushort LastCastTarget;

    /// <summary>The handle our melee auto-attack (BASHSTART) was last started on</summary>
    internal volatile ushort BashTarget;

    /// <summary>When we last re-issued BASHSTART in response to a CEASE_FIRE</summary>

    /// <summary>Whether we believe the character is in BATTLE mode (NC_ACT_CHANGEMODE_REQ 0x02)</summary>
    internal volatile bool InBattleMode;
    /// <summary>When we last sent a change-mode request — a send-rate guard only</summary>
    internal DateTime LastBattleModeSentUtc = DateTime.MinValue;

    /// <summary>Set by the travel driver while a GATE HOP is being taken, and honoured by the Lua's mountUp() via bot.noMount(…</summary>
    public Metrics.MetricStore Metrics { get; } = new();

    /// <summary>Rolling position trace (1/sec, timestamp+map+coord) for the live browser heatmap</summary>
    public Metrics.PositionTrace Trace { get; } = new();

    internal volatile bool SuppressMount;

    /// <summary>Throttle for the "walk SUPPRESSED — cast bar open" line (see BotManager.WalkAsync)</summary>
    internal DateTime LastCastBarWalkLogUtc = DateTime.MinValue;

    /// <summary>Throttle for the "HP stone still on cooldown" line (see BotManager.UseSoulStoneHpAsync)</summary>
    internal DateTime LastStoneCooldownLogUtc = DateTime.MinValue;

    /// <summary>Last known FACING direction as a unit vector, tracked so a cast can tell whether a face-step is actually neede…</summary>
    internal double FacingDx, FacingDy;

    /// <summary>Commit a movement: advance the tracked position AND the tracked facing, because moving from A to B IS what tur…</summary>
    internal void CommitMove(uint fromX, uint fromY, uint toX, uint toY)
    {
        SetFacing(fromX, fromY, toX, toY);
        SetPosition(toX, toY);
    }

    internal void SetFacing(uint fromX, uint fromY, uint toX, uint toY)
    {
        double dx = (double)toX - fromX, dy = (double)toY - fromY;
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d > 1) { FacingDx = dx / d; FacingDy = dy / d; }   // sub-unit hops carry no reliable direction
    }

    /// <summary>Facing as a compass angle in degrees (0-360), or -1 when nothing has set a heading yet</summary>
    public double FacingDeg
    {
        get
        {
            if (FacingDx == 0 && FacingDy == 0) return -1;
            var deg = Math.Atan2(FacingDy, FacingDx) * 180.0 / Math.PI;
            return deg < 0 ? deg + 360.0 : deg;
        }
    }

    /// <summary>What the driver is working on RIGHT NOW, as the driver itself sees it — the quest it has focused, the phase it…</summary>
    public BotFocus? Focus { get; internal set; }

    /// <summary>Key for durable knowledge that belongs to THIS CHARACTER, not to the server</summary>
    public string KnowledgeScope =>
        $"{Options.Host}|{CharName ?? Options.Character ?? Id}";

    /// <summary>Cancellation for the currently-running , if any — cancelled to abort a walk early</summary>
    /// <summary>The waypoints of the walk currently in flight, first to last, or null when nothing is walking.
    /// The minimap draws where the bot IS; without this it cannot draw where the bot is trying to GO, so "is it even
    /// heading anywhere?" is only answerable by reading the tail. The list is already computed by PathFinder.Simplify
    /// and handed to WalkPath — this just keeps hold of it instead of dropping it on the floor.</summary>
    public IReadOnlyList<(uint X, uint Y)>? WalkPlan { get; internal set; }

    internal CancellationTokenSource? WalkCts { get; set; }

    /// <summary>Cancellation for the currently-running follow loop (chase a target player), if any</summary>
    internal CancellationTokenSource? FollowCts { get; set; }

    /// <summary>Cancellation for the currently-running autonomous travel (multi-map ) loop, if any</summary>
    internal CancellationTokenSource? TravelCts { get; set; }

    /// <summary>The bot's current walk speed in world-units per second, driven by MOVESPEED broadcasts (0x203E / 0xCC0D)</summary>
    public double WalkSpeed { get; set; } = 120.0;

    /// <summary>The map name the bot is *intentionally* travelling into (set by the travel loop right before it takes a gate)</summary>
    internal volatile string? PendingDestMap;

    private int _mapChangeSeq;
    private long _lastMapChangeTicks = -1;

    /// <summary>Monotonic counter bumped once per map transition (gate / town portal, in-band or cross-server)</summary>
    /// <summary>TRUE from the moment a CROSS-SERVER map change is seen until the new zone link is actually live.
    /// Phase and ZoneSession cannot answer this: on a handoff the old values stay set for the ~2.3s the teardown and
    /// re-login take, so anything that checks "am I in zone" during that window gets a STALE yes and sends packets
    /// into a dead session. Measured 2026-08-19: the zone dropped at 08:13:58.346, the travel loop's re-entry wait
    /// passed at 08:13:58.477, and MAP_LOGIN_ACK did not land until 08:14:00.736.</summary>
    public bool HandoffInFlight
    {
        get => Volatile.Read(ref _handoffInFlight) != 0;
        internal set => Volatile.Write(ref _handoffInFlight, value ? 1 : 0);
    }
    private int _handoffInFlight;

    public int MapChangeSeq => Volatile.Read(ref _mapChangeSeq);
    internal void BumpMapChange()
    {
        Interlocked.Increment(ref _mapChangeSeq);
        Volatile.Write(ref _lastMapChangeTicks, Environment.TickCount64);
    }

    /// <summary>Milliseconds since the last map transition began (BumpMapChange), or a large number if none yet</summary>
    public long MsSinceMapChange =>
        Volatile.Read(ref _lastMapChangeTicks) is var t && t < 0 ? long.MaxValue : Environment.TickCount64 - t;

    /// <summary>Off-tick pathfinding for this bot. Created on first walkTo; survives BotApi instances.</summary>
    internal Navigation.NavPlanner? NavPlanner { get; set; }

    /// <summary>The Lua behaviour script currently looping on this bot, if any</summary>
    internal Scripting.BotScriptRunner? ScriptRunner { get; set; }

    /// <summary>Behaviour graphs (state machines) running CONCURRENTLY on this bot, keyed by graph name</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, Scripting.BehaviorGraphRunner> GraphRunners { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable per-bot event stream</summary>
    public event Action<BotEvent>? Events;

    /// <summary>Forward an event to the hub</summary>
    internal void Emit(BotEvent e)
    {
        try { Events?.Invoke(e); } catch { /* a subscriber threw — never break the loop */ }
    }

    internal void SetPhase(BotPhase phase) => _phase = phase;
    internal void SetCharName(string name) => _charName = name;
    internal void SetError(string error) => _error = error;

    /// <summary>Raised once per appended log line (the same timestamped text the ring buffer holds)</summary>
    public event Action<string>? LogLine;

    /// <summary>Resolves a mob id to its client MobInfo display name, so log lines read "Marlone (Id 22)" instead of "mob22"</summary>
    public Func<int, string?>? MobNameResolver { get; set; }

    // Matches a BARE `mob ` token only: `mobId=`, `mobs`, `MobInfo` etc
    private static readonly System.Text.RegularExpressions.Regex MobTokenRx =
        new(@"\bmob(\d{1,6})\b", System.Text.RegularExpressions.RegexOptions.Compiled
                               | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // MobInfo lookups are a LINEAR row scan (ShnTable.FindByLong) over a multi-thousand-row table, and Log() runs on…
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _mobNameCache = new();

    /// <summary>Rewrite bare mob ids into "Name (Id N)"</summary>
    private string ResolveMobNames(string message)
    {
        if (MobNameResolver is not { } resolve) return message;
        // Must be case-INSENSITIVE to match the regex below, or a "Mob22" line would be skipped by the very guard meant…
        if (message.IndexOf("mob", StringComparison.OrdinalIgnoreCase) < 0) return message;
        try
        {
            return MobTokenRx.Replace(message, m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out var id)) return m.Value;
                // "" caches a genuine miss so an unknown id costs one scan, not one per line
                var name = _mobNameCache.GetOrAdd(id, i => resolve(i) ?? "");
                return string.IsNullOrWhiteSpace(name) ? m.Value : $"{name} (Id {id})";
            });
        }
        catch { return message; }   // a broken resolver must never cost us the log line
    }

    internal void Log(string message) => Log(BotLogLevel.Note, message);

    /// <summary>Record a HUMAN-INITIATED action on the bot's own tail, at Note level</summary>
    public void LogOperatorAction(string message) => Log(BotLogLevel.Note, message);

    /// <summary>Append a log line at the given verbosity</summary>
    private void TrimLogLocked()
    {
        if (_log.Count <= MaxLogLines && _logChars <= MaxLogChars) return;
        var drop = 0;
        var chars = _logChars;
        var count = _log.Count;
        while ((count - drop > MaxLogLines || chars > MaxLogChars) && drop < count)
        {
            chars -= _log[drop].Line.Length;
            drop++;
        }
        // Take a chunk rather than a line — but never more than a quarter of the buffer, or a burst of very long lines (…
        drop = Math.Min(count, Math.Max(drop, Math.Min(TrimChunk, Math.Max(1, count / 4))));
        // Recount exactly what we are about to remove — `chars` above stopped as soon as it was under budget, and we are…
        long removed = 0;
        for (var i = 0; i < drop; i++) removed += _log[i].Line.Length;
        _log.RemoveRange(0, drop);
        _logChars -= removed;
        if (_logChars < 0) _logChars = 0;
    }

    internal void Log(BotLogLevel level, string message)
    {
        var tag = level switch { BotLogLevel.Verbose => "V", BotLogLevel.Info => "I", _ => "N" };
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {tag} {ResolveMobNames(message)}";
        lock (_logGate)
        {
            _log.Add((level, line));
            _logChars += line.Length;
            TrimLogLocked();
        }
        // Durable copy — flushed per line so the lines just before a crash/SIGTERM survive, which is the case the file e…
        try { LogFile?.Append(line); } catch { }
        // Fan out to live tailers outside the lock; never let a subscriber break logging
        try { LogLine?.Invoke(line); } catch { }
    }

    /// <summary>Lines carried in the polled snapshot</summary>
    private const int SnapshotLogLines = 25;

    private IReadOnlyList<string> RecentLog()
    {
        var buf = new string[SnapshotLogLines];
        var n = 0;
        lock (_logGate)
        {
            for (var i = _log.Count - 1; i >= 0 && n < SnapshotLogLines; i--)
                if (_log[i].Level <= BotLogLevel.Info) buf[n++] = _log[i].Line;
        }
        Array.Reverse(buf, 0, n);          // oldest-first, as callers have always seen it
        return n == SnapshotLogLines ? buf : buf[..n];
    }

    /// <summary>The most recent log lines at or quieter than (Note ⊂ Info ⊂ Verbose)</summary>
    public IReadOnlyList<string> RecentLines(int max, BotLogLevel maxLevel = BotLogLevel.Verbose,
        string? from = null, string? to = null)
    {
        static string? Stamp(string? t, char pad)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            var v = t.Trim();
            // Accept HH:mm, HH:mm:ss, HH:mm:ss.fff — pad out to the stored 12-char prefix
            if (v.Length == 5) v += pad == '0' ? ":00.000" : ":59.999";
            else if (v.Length == 8) v += pad == '0' ? ".000" : ".999";
            return v.Length >= 12 ? v[..12] : v.PadRight(12, pad);
        }
        var lo = Stamp(from, '0');
        var hi = Stamp(to, '9');
        // Walk BACKWARDS and stop once `max` matches are in hand
        bool Match((BotLogLevel Level, string Line) e) =>
            e.Level <= maxLevel
            && ((lo is null && hi is null)
                || (e.Line.Length >= 12
                    && (lo is null || string.CompareOrdinal(e.Line, 0, lo, 0, 12) >= 0)
                    && (hi is null || string.CompareOrdinal(e.Line, 0, hi, 0, 12) <= 0)));

        var outp = new List<string>(max > 0 ? Math.Min(max, 4096) : 256);
        lock (_logGate)
        {
            for (var i = _log.Count - 1; i >= 0; i--)
            {
                if (!Match(_log[i])) continue;
                outp.Add(_log[i].Line);
                if (max > 0 && outp.Count >= max) break;
            }
        }
        outp.Reverse();   // chronological, as callers expect
        return outp;
    }

    /// <summary>A consistent, serializable point-in-time view for the API</summary>
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
            Class: _class == 0 ? null : _class,
            Exp: view is { Exp: >= 0 } ? view.Exp : null,
            SessionExp: view?.SessionExpGained ?? 0,
            Connected: state?.Connected ?? false,
            InboundFrames: state?.InboundCount ?? 0,
            Heartbeats: state?.HeartbeatCount ?? 0,
            LastOpcode: state is { } s ? $"0x{s.LastOpcode:X4}" : null,
            UptimeSeconds: state is { } u ? Math.Round(u.Uptime.TotalSeconds, 1) : 0,
            WmLink: WmSession?.State is { } w
                ? new WmLinkInfo(w.Connected, w.InboundCount, w.HeartbeatCount,
                                 $"0x{w.LastOpcode:X4}", $"0x{WmSession.LastSentOpcode:X4}",
                                 Math.Round(w.Uptime.TotalSeconds, 1), w.DisconnectReason)
                : null,
            DisconnectReason: state?.DisconnectReason,
            Error: Error,
            NearbyPlayers: view?.NearbyCount ?? 0,
            LastChat: view?.LastChat is { } c ? $"<{c.SenderName ?? $"h{c.Handle}"}> {c.Text}" : null,
            Position: Position is { } p ? $"{p.X},{p.Y}" : null,
            Map: CurrentMap,
            MapId: CurrentMapId,
            MapSource: CurrentMapSource,
            Mounted: view?.IsMounted ?? false,
            Hp: view?.Hp,
            Sp: view?.Sp,
            MaxHp: view is { MaxHp: > 0 } ? view.MaxHp : null,
            MaxSp: view is { MaxSp: > 0 } ? view.MaxSp : null,
            HpStones: view?.HpStones,
            SpStones: view?.SpStones,
            InCombat: view?.InCombat ?? false,
            Aggressors: view?.Aggressors.Count ?? 0,
            NearestAggressorDist: NearestAggressorDistance(view),
            Dead: view?.Dead ?? false,
            Drops: view?.Drops.Count ?? 0,
            Script: GraphRunners.IsEmpty
                ? ScriptRunner?.StatusLine
                : string.Join(" | ", GraphRunners.Values.Select(g => g.StatusLine)),
            CreatedAtUtc: CreatedAtUtc,
            RecentLog: RecentLog());
    }

    /// <summary>Distance to the nearest mob currently aggroing us, or null if nothing is aggroed / we don't have a position /…</summary>
    private double? NearestAggressorDistance(ZoneView? view)
    {
        if (view is null || Position is not { } p) return null;
        var aggro = view.Aggressors;
        if (aggro.Count == 0) return null;
        double? best = null;
        foreach (var n in view.NearbyNpcs)
        {
            if (!aggro.Contains(n.Handle)) continue;
            var d = Math.Sqrt(Math.Pow((double)n.X - p.X, 2) + Math.Pow((double)n.Y - p.Y, 2));
            if (best is null || d < best) best = d;
        }
        return best;
    }
}

/// <summary>Serializable point-in-time view of a bot, returned by the control API</summary>
public sealed record BotSnapshot(
    string Id,
    string Phase,
    string Host,
    string Username,
    string? Character,
    uint? Level,
    int? Class,
    long? Exp,
    /// <summary>Exp accumulated since this zone session started</summary>
    long SessionExp,
    bool Connected,
    long InboundFrames,
    long Heartbeats,
    string? LastOpcode,
    double UptimeSeconds,
    /// <summary>The WORLD-MANAGER link, reported separately from the zone link above</summary>
    WmLinkInfo? WmLink,
    string? DisconnectReason,
    string? Error,
    int NearbyPlayers,
    string? LastChat,
    string? Position,
    string? Map,
    // The raw id the server gave us and HOW the name was derived. A name/id disagreement is the
    // wrong-.shbd wedge, and it was previously undiagnosable from the API.
    int? MapId,
    string? MapSource,
    bool Mounted,
    uint? Hp,
    uint? Sp,
    uint? MaxHp,
    uint? MaxSp,
    int? HpStones,
    int? SpStones,
    bool InCombat,
    /// <summary>How many mobs are confidently aggroing us right now (ZoneView.Aggressors, 8s window)</summary>
    int Aggressors,
    /// <summary>Distance to the CLOSEST current aggressor, or null if none/unknown</summary>
    double? NearestAggressorDist,
    bool Dead,
    int Drops,
    string? Script,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> RecentLog);

/// <summary>The driver's self-reported current intent — see</summary>
public sealed record BotFocus(int QuestId, string Phase, string Destination, string Reason, long AtUnixMs);

/// <summary>The world-manager link's own liveness, exposed so the WM connection can be OBSERVED rather than inferred</summary>
public sealed record WmLinkInfo(bool Connected, long InboundFrames, long Heartbeats,
                                string LastOpcode, string LastSent, double UptimeSeconds, string? DisconnectReason);
