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
    // Big enough that a verbose firehose does not evict history before anyone reads it. 1500 was FAR too
    // small: measured 2026-08-05, at level=verbose 1500 lines spanned 63 SECONDS and the endpoint's
    // 200-line default spanned NINE. That produced real misdiagnoses — a "0 kills, the bot completes
    // nothing" report that was a 9-second sample taken during a town phase, and a bag census evicted
    // before it could be read. A narrow sample is not a negative result, so the buffer must be wide
    // enough that "I did not see X" actually means something.
    // 100k lines ~= 10MB, which is nothing next to what this process already holds (operator 2026-08-05:
    // "we'll survive having 10MB worth of buffer"). Still filtered per-level on the way out, so a
    // level=note read stays cheap.
    private const int MaxLogLines = 100_000;
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

    /// <summary>Whether the LAST quest-dialogue drive (DriveQuestDialogueAsync) reached its terminal page
    /// (Qsc 0x06 ACCEPT / 0x0A DONE) for OUR quest — i.e. the accept/hand-in actually CONCLUDED on the wire.
    /// The leveler reads this after a hand-in drive to know the hand-in succeeded even when a REPEATABLE quest
    /// immediately re-accepted (so bot.questProgress reads a stale 10/10 and questReadyToHandin loops).</summary>
    /// <summary>When we last sent BASHSTART. A cast issued before the swing windup elapses (median 418ms,
    /// measured) trades the whole swing for the cast — see docs/COMBAT_BIBLE.md.</summary>
    public DateTime LastBashSentUtc { get; internal set; } = DateTime.MinValue;

    /// <summary>The handle we have already told the server we are targeting. Re-asserting the same target
    /// before every cast is traffic the real client does not send, in the window the swing needs.</summary>
    public ushort CurrentTarget { get; internal set; }

    /// <summary>True while we believe the SERVER still holds <see cref="CurrentTarget"/> as our target.
    ///
    /// <para>⛔ TARGETING IS SERVER STATE AND IT GETS CLEARED WITHOUT ASKING US. BASHSTART (0x242B) carries
    /// NO target — payload is 0 bytes — so it attacks whatever the server currently has selected, and a cast
    /// is only accepted against that same selection (operator 2026-08-12: "auto attack requires a targetting
    /// packet FIRST which must be aimed at this clone ... you can only cast skills on and auto attack the
    /// exact enemy you are currently targetting").</para>
    /// <para>The "only re-target when it changes" optimisation therefore needs an invalidation signal, and it
    /// had none: CurrentTarget was never reset anywhere. After a death, respawn, map handoff or the target
    /// dying, the bot kept believing it was targeted, skipped the targeting packet, and sent bare BASHSTARTs
    /// the server had no target for — a swing-less bash. Handles are per-map too, so a retained handle is not
    /// merely stale, it can name a different entity entirely.</para>
    /// <para>Kept as a separate flag rather than resetting CurrentTarget to 0, because 0 is a legitimate
    /// entity handle and must never be overloaded as "none".</para></summary>
    public bool TargetAsserted { get; internal set; }

    /// <summary>When <see cref="CurrentTarget"/> was last (re-)asserted to the server. "How long have we
    /// been holding this selection" separates a fight from target-thrash, and separates "we just picked it"
    /// from "we have been pointed at a handle we cannot see for two minutes". MinValue = never targeted.</summary>
    public DateTime TargetSetAtUtc { get; internal set; } = DateTime.MinValue;

    // ── STRUCTURED EVENT STREAM ──────────────────────────────────────────────────────────────────
    // Operator 2026-08-12: "build your events endpoint. You really need [it]." Correct, and today is the
    // argument for it. Every wrong call I made came from reasoning over PROSE: grepping log text for a
    // pattern, counting matches, and generalising from a window. That produced, in one session: a dead-bash
    // rate computed against the wrong self-handle, "storage is full" then "storage is not full" then full
    // again, a proxy round-robin theory killed by one question, and "the mage never casts" drawn from a
    // stale packet-log FILE while the live one showed it targeting, bashing and casting correctly.
    // Typed events make "how often / how long / what preceded it" a query instead of a regex I rewrite
    // (and get subtly wrong) each time.
    public sealed record BotEventRec(DateTime AtUtc, string Kind, string Detail);

    private readonly List<BotEventRec> _events = new();
    private const int MaxEvents = 20_000;

    /// <summary>Record a typed event. Cheap and lock-scoped — call it from the same place that logs the
    /// human-readable line, so the two can never drift apart.</summary>
    public void NoteEvent(string kind, string detail = "")
    {
        lock (_events)
        {
            _events.Add(new BotEventRec(DateTime.UtcNow, kind, detail));
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    /// <summary>Events, oldest first, optionally filtered by kind.</summary>
    public IReadOnlyList<BotEventRec> EventLog(string? kind = null)
    {
        lock (_events)
            return kind is null ? _events.ToArray()
                 : _events.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>The server has (or may have) dropped our target — re-assert before the next attack.</summary>
    public void InvalidateTarget(string why)
    {
        if (!TargetAsserted) return;
        TargetAsserted = false;
        Log(BotLogLevel.Verbose, $"[target] invalidated ({why}) — will re-send TARGETTING before the next attack");
    }

    // ── AUTO-RELOG PACING ────────────────────────────────────────────────────────────────────────
    // A zone drop used to trigger an INSTANT relog, which races the server's own session cleanup: the
    // new login completes, the server still holds the old zone session, and it closes the new one
    // immediately (`uptime 0s, lastSENT=0x0000`). That relogs again, and so on.
    // Measured 2026-08-12 on JcqFresh: 23 of 26 relog gaps were under 10s (2.0-7.3s), turning ONE real
    // disconnect into a ~2-minute storm of ~20 logins — each re-seeding 33 quests, 27 skills, 36 items
    // and restarting the driver's town pass. JcqArcher: 243 peer-closes, 100 relogs, 155 sessions dead
    // inside 3 seconds. The bots always recovered; the cost was the thrash.
    // This is not a retry-cap crutch: waiting for the server to release the previous session is the
    // correct SEQUENCING for a protocol that binds one session per character.
    /// <summary>When this bot last completed zone entry. A session that dies seconds after this is the
    /// server refusing a login whose predecessor is still registered — see the relog pacing note.</summary>
    public DateTime ZoneEnteredUtc { get; internal set; } = DateTime.MinValue;

    public DateTime LastRelogUtc { get; internal set; } = DateTime.MinValue;
    public int ShortSessionStreak { get; internal set; }

    // ── WHERE THE HOURS ACTUALLY GO ──────────────────────────────────────────────────────────────
    // Operator 2026-08-12: "ALL 3 lvl 19 bots were in town when I just checked on them. That's
    // abnormal." There was no way to answer it: the driver tallies phase time in a SCRIPT-LOCAL table,
    // so it resets on every script re-apply and only ever surfaces in a periodic log line -- and the
    // note ring buffer covers ~21 minutes at current density, far too short to see where a NIGHT went.
    // Accumulating here makes it survive script re-applies and be queryable at any time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _phaseSeconds = new();
    private string? _currentPhase;
    private DateTime _phaseSinceUtc = DateTime.UtcNow;
    private readonly object _phaseGate = new();

    /// <summary>Seconds spent in each driver phase since this bot started, newest total wins.</summary>
    public IReadOnlyDictionary<string, double> PhaseSeconds => _phaseSeconds;

    /// <summary>The phase the driver says it is in, and how long it has been there.</summary>
    public (string? Phase, double Seconds) CurrentPhase
    {
        get { lock (_phaseGate) return (_currentPhase, (DateTime.UtcNow - _phaseSinceUtc).TotalSeconds); }
    }

    /// <summary>Called by the driver whenever it sets its phase. Closes the previous phase's clock and
    /// opens the new one. Re-reporting the SAME phase is not a no-op — it keeps the running total
    /// current, so a phase the bot never leaves still accrues instead of showing zero.</summary>
    /// <summary>One phase visit: when it started (wall clock), how long it lasted, and what the driver
    /// said it was doing. The RAW RECORD, not a rollup — the operator's call (2026-08-12): "phases should
    /// instead show a json array of ALL PHASE CHANGES with reason and wall time and time in phase. You can
    /// use a script to derive metrics from that."
    /// <para>Cumulative-seconds-per-phase actively misled: `restock = 46 min` read as ONE stall when it was
    /// ~15 interrupted trips. A rollup cannot distinguish slow from repeated; the transition list can, and
    /// any other metric (count, p50, p90) is derivable from it. So store events, derive rollups.</para></summary>
    public sealed record PhaseVisit(string Phase, DateTime StartedUtc, double Seconds);

    private readonly List<PhaseVisit> _phaseLog = new();
    private const int MaxPhaseVisits = 5000;   // ~days of transitions; bounded so it cannot grow forever

    /// <summary>Every phase visit, oldest first, including the one currently open (its Seconds is live).</summary>
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
                // Record the VISIT only when the phase actually changes — NotePhase is called every tick
                // to keep the open phase's clock current, and logging each tick would bury the signal.
                if (!string.Equals(prev, phase, StringComparison.Ordinal))
                {
                    _phaseLog.Add(new PhaseVisit(prev, _phaseSinceUtc, d));
                    NoteEvent("phase", $"{prev} -> {phase} after {d:F1}s");
                    if (_phaseLog.Count > MaxPhaseVisits) _phaseLog.RemoveRange(0, _phaseLog.Count - MaxPhaseVisits);
                }
            }
            if (!string.Equals(_currentPhase, phase, StringComparison.Ordinal)) _phaseSinceUtc = now;
            _currentPhase = phase;
            // Flush at most every 30s: frequent enough that a pod kill loses seconds, not hours, and
            // rare enough that a per-tick call does not hammer NFS.
            if ((now - _phaseFlushUtc).TotalSeconds >= 30) { _phaseFlushUtc = now; flush = true; }
        }
        if (flush) PhasePersist?.Invoke(Id, PhaseSeconds);
    }

    private DateTime _phaseFlushUtc = DateTime.MinValue;

    /// <summary>Set by the manager so phase totals reach durable storage — see the note on
    /// NpcKnowledge.SavePhaseSeconds for why losing them on respawn is unacceptable.</summary>
    public Action<string, IReadOnlyDictionary<string, double>>? PhasePersist { get; set; }

    /// <summary>Carry forward totals from earlier runs of this same bot.</summary>
    /// <summary>The on-disk mirror of this bot's tail. Set by the manager at spawn.</summary>
    internal BotLogFile? LogFile { get; set; }

    /// <summary>Load history from BEFORE this process started into the in-memory ring, so /log and the
    /// snapshot endpoints show a continuous story across pod restarts instead of beginning at zero.
    /// Marked so a reader can tell restored lines from live ones and does not mistake them for now.</summary>
    internal void SeedLogFromDisk(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        lock (_logGate)
        {
            var restored = new List<(BotLogLevel Level, string Line)>(lines.Count + 1);
            foreach (var l in lines)
            {
                // "HH:mm:ss.fff <TAG> …" — recover the level so ?level= filtering still works on history.
                var lvl = BotLogLevel.Note;
                var t = l.Length > 13 ? l[13] : 'N';
                if (t == 'V') lvl = BotLogLevel.Verbose; else if (t == 'I') lvl = BotLogLevel.Info;
                restored.Add((lvl, l));
            }
            restored.Add((BotLogLevel.Note, $"{DateTime.UtcNow:HH:mm:ss.fff} N ── {lines.Count} line(s) above restored from disk (previous session) ──"));
            _log.InsertRange(0, restored);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
    }

    public void SeedPhaseSeconds(IReadOnlyDictionary<string, double> prior)
    {
        foreach (var (k, v) in prior) if (v > 0) _phaseSeconds.AddOrUpdate(k, v, (_, old) => old + v);
    }

    public bool LastDialogConcluded { get; internal set; }

    /// <summary>The last-applied Lua script (name/source/tick) — kept so a self-relog (bot.relog / stuck
    /// instance recovery) can re-apply the same behaviour after the clean logout + re-spawn.</summary>
    public string? LastScriptName { get; internal set; }
    public string? LastScriptSource { get; internal set; }
    public int LastScriptTickMs { get; internal set; }

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

    /// <summary>ALWAYS-ON bounded capture of the last 100 frames, both directions. Unlike
    /// <see cref="PacketLog"/> (opt-in, writes everything to disk) this is running from the first
    /// connect, so a post-mortem can show the wire without anyone having predicted the failure.</summary>
    internal Net.PacketRing PacketRing { get; } = new(100);

    /// <summary>The tap to install on every session: feeds the always-on ring, and the file log too when
    /// one is enabled. Reads <see cref="PacketLog"/> per frame (not captured once) so enabling or
    /// disabling the file log mid-session takes effect without re-installing the tap.</summary>
    internal Action<bool, ushort, ReadOnlyMemory<byte>> CombinedTap =>
        (outbound, opcode, payload) =>
        {
            PacketRing.Tap(outbound, opcode, payload);
            PacketLog?.Tap(outbound, opcode, payload);
        };

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

    /// <summary>Live party roster, keyed by member char-name — the FOUNDATION for TEAMWORK coordination
    /// (cleric team-heal reads Hp/MaxHp, composition reads ChrClass/Level, regroup/shared-kill read X/Y).
    /// Populated from the WM party member-state packets: NC_PARTY_MEMBER_LIST_CMD(9)/MEMBERINFORM_CMD(50)
    /// carry name+hp/sp; MEMBERCLASS_CMD(51) carries class/level/maxhp/maxsp; MEMBERLOCATION_CMD(73) carries
    /// positions (layout TBD — pinned from a 2-bot capture). Cleared on party leave/dismiss. Thread-safe
    /// (written on the WM read thread, read on the Lua tick).</summary>
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

    internal void SetPosition(uint x, uint y)
    {
        (uint X, uint Y)? prev;
        lock (_posGate) { prev = _pos; _pos = (x, y); }
        // 📍 Every position update flows through here — walk steps, MOVEFAIL resyncs, map handoffs — so it is
        // the one place the trace and the distance metric can be fed without hunting call sites.
        // Trace self-rate-limits to 1/sec; DISTANCE is only counted for same-map moves under a sane step cap,
        // because a map handoff teleports the coordinate and would otherwise book a bogus multi-thousand-unit
        // "journey" every time the bot changes zone.
        Trace.Sample(CurrentMap, (int)x, (int)y);
        if (prev is { } p && !string.IsNullOrEmpty(CurrentMap) && string.Equals(CurrentMap, _lastTraceMap, StringComparison.Ordinal))
        {
            var d = Math.Sqrt(Math.Pow((double)x - p.X, 2) + Math.Pow((double)y - p.Y, 2));
            if (d > 0 && d < 2000) Metrics.LogMetric("distance", d);
        }
        _lastTraceMap = CurrentMap;
    }
    private string? _lastTraceMap;

    /// <summary>The target of the most recently issued MOVERUN step (the tile the bot was trying to
    /// enter). On a MOVEFAIL, this is the tile the server rejected — the nav layer marks it
    /// runtime-blocked so the pathfinder routes around it (see BlockGrid.MarkBlocked).</summary>
    public (uint X, uint Y)? LastMoveTarget { get; internal set; }

    /// <summary>MOVEFAIL-streak tracking for the perpendicular-to-wall UNSTICK (operator 2026-07-13): the
    /// snap-back position of the last MOVEFAIL and how many in a row landed at ~the same spot. When the bot
    /// is wedged against a wall (repeated MOVEFAILs at one position, e.g. walking straight into it), the nav
    /// layer nudges it perpendicular to the wall to slide free.</summary>
    public (uint X, uint Y)? LastMoveFailPos { get; internal set; }
    public int MoveFailStreak { get; internal set; }
    public DateTime LastUnstickUtc { get; internal set; }

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

    /// <summary>The handle our melee auto-attack (BASHSTART) was last started on. Used to re-issue
    /// BASHSTART on the SAME target when the server cease-fires us mid-fight (every skill cast's
    /// STOP cancels the swing stream), so auto-attack damage actually accumulates.</summary>
    internal volatile ushort BashTarget;

    /// <summary>When we last re-issued BASHSTART in response to a CEASE_FIRE. Paces the restart —
    /// the server can send several CEASE_FIRE for one cancellation.</summary>

    /// <summary>Whether we believe the character is in BATTLE mode (NC_ACT_CHANGEMODE_REQ 0x02).
    /// A persistent toggle — see <c>EnsureBattleModeAsync</c>. Cleared on death / map change so it
    /// is re-asserted only when it can genuinely have lapsed.</summary>
    internal volatile bool InBattleMode;
    /// <summary>When we last sent a change-mode request — a send-rate guard only. The AUTHORITY on battle
    /// mode is ZoneView.SelfInBattleMode, fed by the server's 0x2009 broadcast.</summary>
    internal DateTime LastBattleModeSentUtc = DateTime.MinValue;

    /// <summary>Set by the travel driver while a GATE HOP is being taken, and honoured by the Lua's
    /// <c>mountUp()</c> via <c>bot.noMount()</c>. A gate is silently ignored while mounted, and the Lua
    /// tick mounts for transit speed independently of C# — so without this the two race: the Lua mounts
    /// during the gate approach, the RIDE_ON ack lands after C#'s mounted-check, and the gate is clicked
    /// mounted anyway (observed 2026-08-04 19:47: mounted at .963, gate clicked at 19:47:10.033, hop
    /// aborted). Suppressing the mount is what removes the race — a re-check alone cannot.</summary>
    /// <summary>Per-bot metrics ("a window into everything going on with the bot" — operator 2026-08-05).
    /// Declare with InitMetric, write with LogMetric from anywhere; batching absorbs the caller's rate.</summary>
    public Metrics.MetricStore Metrics { get; } = new();

    /// <summary>Rolling position trace (1/sec, timestamp+map+coord) for the live browser heatmap.</summary>
    public Metrics.PositionTrace Trace { get; } = new();

    internal volatile bool SuppressMount;

    /// <summary>Throttle for the "walk SUPPRESSED — cast bar open" line (see BotManager.WalkAsync).</summary>
    internal DateTime LastCastBarWalkLogUtc = DateTime.MinValue;

    /// <summary>Throttle for the "HP stone still on cooldown" line (see BotManager.UseSoulStoneHpAsync).</summary>
    internal DateTime LastStoneCooldownLogUtc = DateTime.MinValue;

    /// <summary>Last known FACING direction as a unit vector, tracked so a cast can tell whether a
    /// face-step is actually needed. Set whenever we commit a facing (FaceAndStop, or a BASHSTART on a
    /// target). The MOVERUN face-step breaks the melee swing stream, so it must only be sent when the
    /// facing/range genuinely needs adjusting (operator 2026-08-04).</summary>
    internal double FacingDx, FacingDy;

    /// <summary>
    /// Commit a movement: advance the tracked position AND the tracked facing, because moving from A to B
    /// IS what turns the character to face B — the same authority as an explicit face-step.
    ///
    /// ⚠️ This exists because facing was previously updated ONLY in FaceAndStopAsync. Every ordinary walk
    /// step committed position and left the heading untouched, so after any walk the "facing" used by the
    /// UsableDegree arc check described where we were last deliberately pointed, which could be anywhere.
    /// A stale heading makes the arc test answer a question about the past.
    /// </summary>
    internal void CommitMove(uint fromX, uint fromY, uint toX, uint toY)
    {
        double dx = (double)toX - fromX, dy = (double)toY - fromY;
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d > 1) { FacingDx = dx / d; FacingDy = dy / d; }   // sub-unit hops carry no reliable direction
        SetPosition(toX, toY);
    }

    /// <summary>Facing as a compass angle in degrees (0-360), or -1 when nothing has set a heading yet.
    /// ⚠️ This is OUR heading in game-coordinate space (atan2 of the facing vector). It is NOT in the same
    /// units as a mob's <c>dir</c> byte (0-255) from SHINE_COORD_TYPE — that scale has not been pinned yet,
    /// so the two are reported side by side but must not be compared numerically.</summary>
    public double FacingDeg
    {
        get
        {
            if (FacingDx == 0 && FacingDy == 0) return -1;
            var deg = Math.Atan2(FacingDy, FacingDx) * 180.0 / Math.PI;
            return deg < 0 ? deg + 360.0 : deg;
        }
    }

    /// <summary>What the driver is working on RIGHT NOW, as the driver itself sees it — the quest it has
    /// focused, the phase it is in, and (when travelling) where it is heading and why.
    /// <para>⚠️ This is the DRIVER's own answer, published by the Lua as it decides. It is NOT the host's
    /// re-derivation: the quest board the page renders is ordered by a rule that merely <i>mirrors</i> the
    /// driver's sort and is documented as "roughly what it will pick next" — which cannot answer "which
    /// quest is it actually on, and why is it walking there". Operator 2026-08-06: "mark the current target
    /// quest in the list … I wanna be able to see *why* the bot is currently travelling or where it's
    /// travelling to." A second copy of the decision logic would drift and lie; the driver reporting itself
    /// cannot.</para>
    /// <para>Null until the driver has published anything (a bot with no script, or one that has not yet
    /// reached its first decision) — which is NOT the same as "idle".</para></summary>
    public BotFocus? Focus { get; internal set; }

    /// <summary>Key for durable knowledge that belongs to THIS CHARACTER, not to the server.
    /// <para>⛔ The learned stores were keyed by HOST ALONE, which was harmless with one bot and became a
    /// correctness bug the moment five ran side by side on 2026-08-06: every bot on <c>fiesta-proxy</c>
    /// shared one bucket, so a level-1 Mage was seeded with a level-26 Priest's soul-stone heal capacity
    /// and melee range — and wrote its own back over them. The operator saw it first as "heal cap shows
    /// the same JcqFresh value for every bot"; that display was telling the truth about the data.</para>
    /// <para>Quest deprioritization and death counts are the worse half: one character's "this quest
    /// killed me" verdict applied to every other character, including ones 25 levels below it.</para>
    /// <para>Use this for anything learned ABOUT the character (heal capacity, melee range, quest
    /// verdicts, per-mob threat). Keep plain <see cref="BotSpawnOptions.Host"/> for facts about the WORLD
    /// that every character shares — where a shop NPC stands, which items the warehouse refuses.</para>
    /// <para>Falls back through CharName → the requested Character → the bot id, so a bot that has not yet
    /// selected a character still gets a stable, non-colliding scope rather than silently sharing one.</para></summary>
    public string KnowledgeScope =>
        $"{Options.Host}|{CharName ?? Options.Character ?? Id}";

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
    private long _lastMapChangeTicks = -1;

    /// <summary>Monotonic counter bumped once per map transition (gate / town portal,
    /// in-band or cross-server). The travel loop snapshots it before taking a gate and
    /// waits for it to advance — a transition-agnostic "did the warp land?" signal that
    /// survives the cross-server reconnect (which swaps the ZoneView out).</summary>
    public int MapChangeSeq => Volatile.Read(ref _mapChangeSeq);
    internal void BumpMapChange()
    {
        Interlocked.Increment(ref _mapChangeSeq);
        Volatile.Write(ref _lastMapChangeTicks, Environment.TickCount64);
    }

    /// <summary>Milliseconds since the last map transition began (BumpMapChange), or a large
    /// number if none yet. Used to gate gate-EDGE learning: during/just after a transition the
    /// ZoneView can briefly carry the NEW map's gates while <see cref="CurrentMap"/> hasn't
    /// settled — learning then mis-attributes them to the old map (the bogus RouVal02->Eld
    /// edge). Callers skip <c>ObserveGate</c> until this exceeds a settle window.</summary>
    public long MsSinceMapChange =>
        Volatile.Read(ref _lastMapChangeTicks) is var t && t < 0 ? long.MaxValue : Environment.TickCount64 - t;

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

    /// <summary>Resolves a mob id to its client <c>MobInfo</c> display name, so log lines read
    /// "Marlone (Id 22)" instead of "mob22". Set by the manager from <c>ClientData</c>; null
    /// (or a null/empty result) leaves the token untouched — an unresolvable id is itself signal
    /// that the mob is missing from MobInfo, so it must stay visible rather than be blanked.</summary>
    public Func<int, string?>? MobNameResolver { get; set; }

    // Matches a BARE `mob<digits>` token only: `mobId=`, `mobs`, `MobInfo` etc. don't match
    // (the char after the digits must be a non-word char), and the id is bounded so a hex blob
    // can't produce a silly capture. Compiled once — this runs on every log line (~6/s).
    private static readonly System.Text.RegularExpressions.Regex MobTokenRx =
        new(@"\bmob(\d{1,6})\b", System.Text.RegularExpressions.RegexOptions.Compiled
                               | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // MobInfo lookups are a LINEAR row scan (ShnTable.FindByLong) over a multi-thousand-row table,
    // and Log() runs on the session read loop — so memoize. One scan per distinct id, ever.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _mobNameCache = new();

    /// <summary>Rewrite bare mob ids into "Name (Id N)". Cheap-exits when the line has no
    /// "mob" at all, which is the overwhelming majority of lines.</summary>
    private string ResolveMobNames(string message)
    {
        if (MobNameResolver is not { } resolve) return message;
        // Must be case-INSENSITIVE to match the regex below, or a "Mob22" line would be
        // skipped by the very guard meant only to make the common case cheap.
        if (message.IndexOf("mob", StringComparison.OrdinalIgnoreCase) < 0) return message;
        try
        {
            return MobTokenRx.Replace(message, m =>
            {
                if (!int.TryParse(m.Groups[1].Value, out var id)) return m.Value;
                // "" caches a genuine miss so an unknown id costs one scan, not one per line.
                var name = _mobNameCache.GetOrAdd(id, i => resolve(i) ?? "");
                return string.IsNullOrWhiteSpace(name) ? m.Value : $"{name} (Id {id})";
            });
        }
        catch { return message; }   // a broken resolver must never cost us the log line
    }

    internal void Log(string message) => Log(BotLogLevel.Note, message);

    /// <summary>Record a HUMAN-INITIATED action on the bot's own tail, at Note level.
    /// <para>The tail is the one place the bot's story is read end to end, so an operator override that
    /// appears only in an HTTP response is invisible exactly when it matters — right before the behaviour
    /// change it caused. Narrower than exposing <see cref="Log(string)"/>: the host can record that a human
    /// did something, and nothing else.</para></summary>
    public void LogOperatorAction(string message) => Log(BotLogLevel.Note, message);

    /// <summary>Append a log line at the given verbosity. The level is stamped into the
    /// text (<c>N</c>/<c>I</c>/<c>V</c> after the timestamp) so a raw tail is still readable,
    /// and retained structurally so the snapshot/tail endpoints can filter by level.</summary>
    internal void Log(BotLogLevel level, string message)
    {
        var tag = level switch { BotLogLevel.Verbose => "V", BotLogLevel.Info => "I", _ => "N" };
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {tag} {ResolveMobNames(message)}";
        lock (_logGate)
        {
            _log.Add((level, line));
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        // Durable copy — flushed per line so the lines just before a crash/SIGTERM survive, which is
        // the case the file exists for. Outside the ring lock: disk must never stall logging.
        try { LogFile?.Append(line); } catch { }
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
    /// <summary>Recent log lines, filtered by severity and optionally by a TIME WINDOW.
    /// <para>The window exists for the drill-down workflow (operator 2026-08-05): read <c>level=note</c>,
    /// spot a headline at <c>13:41:22.123</c>, then re-read <c>level=info from=13:41:15 to=13:42:00</c> to
    /// see everything around it — instead of pulling a huge verbose blob and hoping the moment is in it.</para>
    /// <para><paramref name="from"/>/<paramref name="to"/> are UTC times of day, <c>HH:mm[:ss[.fff]]</c>
    /// (partials are padded: <c>13:41</c> → <c>13:41:00.000</c>, and <c>to</c> pads to <c>.999</c> so an
    /// inclusive end works). Lines are stored with an <c>HH:mm:ss.fff</c> prefix, so this is an exact
    /// ordinal compare on that prefix — no parsing, no allocation per line.</para>
    /// <para>⚠️ Does NOT handle a window spanning midnight (times only, no date); such a range returns
    /// nothing rather than wrapping. Say so rather than silently returning a confusing subset.</para></summary>
    public IReadOnlyList<string> RecentLines(int max, BotLogLevel maxLevel = BotLogLevel.Verbose,
        string? from = null, string? to = null)
    {
        static string? Stamp(string? t, char pad)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            var v = t.Trim();
            // Accept HH:mm, HH:mm:ss, HH:mm:ss.fff — pad out to the stored 12-char prefix.
            if (v.Length == 5) v += pad == '0' ? ":00.000" : ":59.999";
            else if (v.Length == 8) v += pad == '0' ? ".000" : ".999";
            return v.Length >= 12 ? v[..12] : v.PadRight(12, pad);
        }
        var lo = Stamp(from, '0');
        var hi = Stamp(to, '9');
        lock (_logGate)
        {
            IEnumerable<(BotLogLevel Level, string Line)> q = _log.Where(e => e.Level <= maxLevel);
            if (lo is not null || hi is not null)
                q = q.Where(e => e.Line.Length >= 12
                    && (lo is null || string.CompareOrdinal(e.Line, 0, lo, 0, 12) >= 0)
                    && (hi is null || string.CompareOrdinal(e.Line, 0, hi, 0, 12) <= 0));
            var filtered = q.Select(e => e.Line).ToList();
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
            Class: _class == 0 ? null : _class,
            Exp: view is { Exp: >= 0 } ? view.Exp : null,
            // Exp gained THIS SESSION. Reported alongside the absolute because the absolute can be
            // genuinely unknown (a login burst that carried no exp seed), and "unknown absolute" must not
            // read as "not progressing" — a bot levelling 4→6 with exp:null looked stalled to the operator.
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

    /// <summary>Distance to the nearest mob currently aggroing us, or null if nothing is aggroed / we don't
    /// have a position / none of the aggressor handles are in the NPC view. Cross-references the aggressor
    /// handle set against the live NPC positions — the same join the Lua side does, lifted to the snapshot so
    /// a deaggro can be observed from the control API (see <see cref="BotSnapshot.NearestAggressorDist"/>).</summary>
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

/// <summary>Serializable point-in-time view of a bot, returned by the control API.</summary>
public sealed record BotSnapshot(
    string Id,
    string Phase,
    string Host,
    string Username,
    string? Character,
    uint? Level,
    int? Class,
    long? Exp,
    /// <summary>Exp accumulated since this zone session started. Always meaningful, even when
    /// <see cref="Exp"/> is null because the login burst carried no seed.</summary>
    long SessionExp,
    bool Connected,
    long InboundFrames,
    long Heartbeats,
    string? LastOpcode,
    double UptimeSeconds,
    /// <summary>The WORLD-MANAGER link, reported separately from the zone link above. Every field on this
    /// snapshot used to come from <c>ZoneSession.State</c> alone, so the WM link — which stays open for the
    /// whole session and which the server DOES heartbeat (0x0804→0x0805; the zone link does not) — was
    /// completely unobservable from the API. That gap is why a run of WM `peer closed` disconnects could
    /// not be diagnosed: `heartbeats: 0` was the ZONE's counter and said nothing about the link that was
    /// actually dying. Null when there is no WM session.</summary>
    WmLinkInfo? WmLink,
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
    /// <summary>How many mobs are confidently aggroing us right now (ZoneView.Aggressors, 8s window).
    /// Exposed on the snapshot because the Lua runtime had <c>bot.aggressors()</c> but nothing outside the
    /// script could see it — which made a "did the tail actually shed?" check impossible from the API.</summary>
    int Aggressors,
    /// <summary>Distance to the CLOSEST current aggressor, or null if none/unknown. This is the field that
    /// lets us MEASURE the mob leash off the wire instead of baking a constant: ride away and watch at what
    /// distance the aggressor set drains without any kills. Needed for the arrival-shed (P0).</summary>
    double? NearestAggressorDist,
    bool Dead,
    int Drops,
    string? Script,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> RecentLog);

/// <summary>The driver's self-reported current intent — see <see cref="BotHandle.Focus"/>.
/// <para>Every field is what the DRIVER said, verbatim; the host neither invents nor validates it. A
/// value of 0/"" means the driver did not report that part (e.g. no quest focused while restocking),
/// which is "not applicable / not said", never "none exists".</para></summary>
/// <param name="QuestId">The quest the driver is currently working, or 0 when the current phase is not
/// quest-driven (a storage trip, a restock, a death recovery).</param>
/// <param name="Phase">The driver's own phase name — the same token it logs as <c>PHASE =&gt; x</c>
/// and accounts time under, so the page and the TIME-BUDGET line speak one vocabulary.</param>
/// <param name="Destination">Where it is heading: a map code for a cross-map route, an NPC/mob name or
/// a coordinate for a local one. Empty when it is not travelling.</param>
/// <param name="Reason">Why — the same reason string <c>travelToLogged()</c> puts in the MAP-CHANGE
/// trail, so a confusing route in the UI can be grepped straight out of the log.</param>
/// <param name="AtUnixMs">When the driver published this, so the page can show staleness rather than
/// presenting a frozen intent as current.</param>
public sealed record BotFocus(int QuestId, string Phase, string Destination, string Reason, long AtUnixMs);

/// <summary>The world-manager link's own liveness, exposed so the WM connection can be OBSERVED rather
/// than inferred. The WM is the link the server heartbeats (<c>0x0804 NC_MISC_HEARTBEAT_REQ</c> →
/// <c>0x0805 NC_MISC_HEARTBEAT_ACK</c>, verified in Z:/LongCaptureNoDc.pcapng on port 9013); the zone
/// link is not heartbeated at all, so a zone-sourced heartbeat counter reading 0 is normal and proves
/// nothing about the WM.</summary>
/// <param name="LastSent">Last opcode WE sent on this link. Includes the raw heartbeat ACK — before that
/// was recorded this always read 0x0000 and looked like we were answering nothing.</param>
public sealed record WmLinkInfo(bool Connected, long InboundFrames, long Heartbeats,
                                string LastOpcode, string LastSent, double UptimeSeconds, string? DisconnectReason);
