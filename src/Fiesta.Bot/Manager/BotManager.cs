using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Navigation;
using Fiesta.Bot.Pathfinding;
using Fiesta.Bot.Scripting;
using Fiesta.Bot.Session;
using Fiesta.Bot.Zone;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Manager;

/// <summary>
/// Owns N bots in parallel, keyed by id. Each <see cref="Spawn"/> kicks off the
/// full login chain on a background task — Login → WM (with optional in-band
/// character creation + tutorial decline) → [1801] zone entry → a long-lived
/// <see cref="BotSession"/> that answers heartbeats until the bot is stopped.
/// The same orchestration <c>LoginTestCli</c> proved end-to-end, minus the hold
/// timer: a managed bot runs until <see cref="StopAsync"/> (or a server kick).
///
/// Spawn returns immediately with a <see cref="BotHandle"/>; callers poll the
/// handle's <see cref="BotHandle.Phase"/>/<see cref="BotHandle.Snapshot"/> for
/// progress. Thread-safe; the control API is the primary caller.
/// </summary>
public sealed class BotManager : IAsyncDisposable
{
    private readonly byte[] _xorTable;
    private readonly Action<string>? _globalLog;
    private readonly ConcurrentDictionary<string, BotHandle> _bots = new(StringComparer.OrdinalIgnoreCase);
    private int _seq;

    /// <summary>The world map graph, learned by play (gates seen in each map) and
    /// shared across all bots — so one bot's exploration helps the rest route.</summary>
    public MapGraph Graph { get; } = new();

    /// <summary>Map id↔short-name resolver, learned from gate links and (optionally)
    /// seeded from a BYO MapInfo dump via <c>MAPINFO_PATH</c>.</summary>
    public MapCatalog Catalog { get; } = new();

    /// <summary>Resolves a map short-name to its walkability grid (BYO, from
    /// <c>BLOCKINFO_DIR</c>). Set by the host so navigation actions (e.g. <see
    /// cref="Follow"/>) can pathfind around obstacles; null = no grid available,
    /// callers fall back to straight-line movement.</summary>
    public Func<string, BlockGrid?>? GridProvider { get; set; }

    /// <summary>BYO client game-data reader (SHN tables from the operator's
    /// <c>ressystem</c>). Lets feature code resolve client-visible data — e.g. a skill's
    /// facing arc / cooldown / mana from <c>ActiveSkill</c> — instead of hard-coding it.
    /// Set by the host; null = no client data dir configured (callers fall back).</summary>
    public GameData.ClientData? ClientData { get; set; }

    public BotManager(byte[] xorTable, Action<string>? globalLog = null)
    {
        _xorTable = xorTable;
        _globalLog = globalLog;
        var seeded = Catalog.LoadSeedFromEnv();
        if (seeded > 0) _globalLog?.Invoke($"[nav] MapCatalog seeded {seeded} maps from MAPINFO_PATH");
    }

    /// <summary>Start a bot. Non-blocking — the login chain runs in the background;
    /// watch the returned handle for progress. Throws only on a duplicate id.</summary>
    public BotHandle Spawn(BotSpawnOptions options)
    {
        var id = options.Id ?? $"b{Interlocked.Increment(ref _seq)}";
        var handle = new BotHandle(id, options);
        if (!_bots.TryAdd(id, handle))
            throw new InvalidOperationException($"a bot with id '{id}' already exists");

        handle.Log($"spawn requested: {options.Host}:{options.LoginPort} user='{options.Credentials.Username}'");
        handle.RunTask = Task.Run(() => RunBotAsync(handle));
        return handle;
    }

    public IReadOnlyList<BotHandle> List() => _bots.Values.OrderBy(b => b.Id).ToArray();

    public BotHandle? Get(string id) => _bots.TryGetValue(id, out var h) ? h : null;

    /// <summary>Toggle a tailable, both-directions, plaintext (XOR-decoded) packet log for a
    /// bot — every S→C and C→S frame, interleaved in arrival order, with opcode + canonical
    /// name + a hex/ASCII dump. Returns (found, enabled, path). Path is the file to
    /// <c>tail -f</c>. Survives cross-server handoffs (re-attached when the zone session swaps).</summary>
    public (bool Found, bool Enabled, string? Path) SetPacketLog(string id, bool enabled)
    {
        if (!_bots.TryGetValue(id, out var handle)) return (false, false, null);

        if (enabled)
        {
            if (handle.PacketLog is { } already)
            {
                // Make sure the current sessions are tapped (e.g. enabled before zone entry).
                if (handle.ZoneSession is { } zs) zs.PacketTap = already.Tap;
                if (handle.WmSession is { } ws) ws.PacketTap = already.Tap;
                return (true, true, already.Path);
            }
            var dir = Environment.GetEnvironmentVariable("PACKETLOG_DIR") ?? Directory.GetCurrentDirectory();
            var path = System.IO.Path.Combine(dir, $"packets-{id}.log");
            var log = new Net.PacketLog(path);
            handle.PacketLog = log;
            if (handle.ZoneSession is { } zs2) zs2.PacketTap = log.Tap;
            if (handle.WmSession is { } ws2) ws2.PacketTap = log.Tap;
            handle.Log($"packet log ENABLED -> {path}");
            return (true, true, path);
        }
        else
        {
            if (handle.ZoneSession is { } zs) zs.PacketTap = null;
            if (handle.WmSession is { } ws) ws.PacketTap = null;
            var path = handle.PacketLog?.Path;
            handle.PacketLog?.Dispose();
            handle.PacketLog = null;
            if (path is not null) handle.Log("packet log DISABLED");
            return (true, false, path);
        }
    }

    /// <summary>Signal a bot to stop and wait (briefly) for it to wind down.
    /// Returns false if no such bot. The handle is removed once stopped.</summary>
    public async Task<bool> StopAsync(string id, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return false;
        handle.Log("stop requested");
        // Tear down any looping behaviour script/graph first so it stops issuing actions.
        handle.ScriptRunner?.Dispose();
        handle.ScriptRunner = null;
        foreach (var g in handle.GraphRunners.Values) g.Dispose();
        handle.GraphRunners.Clear();
        var inZone = handle.Phase == BotPhase.InZone && handle.ZoneSession is { } zs0 && handle.WmSession is not null;
        if (handle.Phase is not (BotPhase.Stopped or BotPhase.Failed))
            handle.SetPhase(BotPhase.Stopping);

        if (inZone)
        {
            // Clean logout: send the quit frames (zone: LOGOUTREADY+quit, WM: quit),
            // then DON'T cancel — keep the sessions running so they answer heartbeats
            // through the server's ~10s logout countdown (combat-logout: needs ~10s
            // with no damage). The server closes both links when it completes, which
            // ends RunTask. Cancelling/closing mid-countdown aborts the logout and
            // leaves the char "online" → the next login is duplicate-kicked.
            using var logoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await handle.ZoneSession!.LogoutAsync(logoutReady: true, logoutCts.Token); } catch { }
            if (handle.WmSession is { } ws) try { await ws.LogoutAsync(logoutReady: false, logoutCts.Token); } catch { }

            if (handle.RunTask is { } t)
            {
                try { await t.WaitAsync(TimeSpan.FromSeconds(14), ct); } // ~10s timer + slack
                catch (TimeoutException) { handle.Log("clean logout didn't complete in 14s — forcing"); handle.Cts.Cancel(); }
                catch (OperationCanceledException) { }
            }
        }
        else
        {
            handle.Cts.Cancel();
        }

        if (handle.RunTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (TimeoutException) { handle.Log("stop: lifecycle task did not finish within 10s"); }
            catch (OperationCanceledException) { }
        }
        _bots.TryRemove(id, out _);
        handle.Cts.Dispose();
        return true;
    }

    // ── Behaviour scripting (Lua) ─────────────────────────────────────────────
    /// <summary>Apply a Lua behaviour script to a bot and start looping it. A new apply
    /// replaces any script already running ("new upload &gt; new script"). Returns the
    /// runner, or null if the bot id is unknown. The runner subscribes to the bot's
    /// stable event hub, runs <c>on_start</c>, then ticks + dispatches events on its own
    /// thread until <see cref="StopScript"/> / <see cref="StopAsync"/>.</summary>
    public BotScriptRunner? ApplyScript(string id, string name, string source, int tickMs = 250, bool trace = false)
    {
        if (!_bots.TryGetValue(id, out var handle)) return null;
        handle.ScriptRunner?.Dispose();               // replace any running script
        void ScriptLog(string m) { handle.Log(m); _globalLog?.Invoke($"[{id}] {m}"); }
        var runner = new BotScriptRunner(handle, new BotApi(this, handle), name, source, ScriptLog, handle.Cts.Token, tickMs, trace);
        handle.ScriptRunner = runner;
        handle.Log($"script '{name}' applied ({source.Length} chars, tick={tickMs}ms{(trace ? ", trace" : "")})");
        runner.Start();
        return runner;
    }

    /// <summary>Stop a bot's looping script (no-op if none). The bot stays in zone.</summary>
    public bool StopScript(string id)
    {
        if (_bots.TryGetValue(id, out var handle) && handle.ScriptRunner is { } r)
        {
            r.Dispose();
            handle.ScriptRunner = null;
            handle.Log("script stopped");
            return true;
        }
        return false;
    }

    /// <summary>Debug status of a bot's running script (null if no bot / no script).</summary>
    public ScriptStatus? ScriptStatus(string id)
        => _bots.TryGetValue(id, out var handle) ? handle.ScriptRunner?.Status() : null;

    // ── Behaviour graph (state machine) ───────────────────────────────────────
    /// <summary>Disk-persisted behaviour-graph library (states + transitions + scripts).
    /// The host may point this at a chosen directory; defaults under the working dir.</summary>
    public Scripting.GraphStore Graphs { get; set; } = new("behavior-graphs");

    /// <summary>Apply a behaviour graph to a bot and start it (replaces any running
    /// script/graph). <paramref name="startState"/> overrides the graph's initial state
    /// (e.g. resume the persisted current state). Returns the runner, or null if unknown bot.</summary>
    public Scripting.BehaviorGraphRunner? ApplyGraph(string id, Scripting.BehaviorGraph graph, string? startState = null, int tickMs = 250)
    {
        if (!_bots.TryGetValue(id, out var handle)) return null;
        if (handle.GraphRunners.TryRemove(graph.Name, out var old)) old.Dispose(); // replace same-named only
        void GLog(string m) { handle.Log(m); _globalLog?.Invoke($"[{id}] {m}"); }
        var runner = new Scripting.BehaviorGraphRunner(handle, new BotApi(this, handle), graph, GLog,
            handle.Cts.Token, tickMs, startState, st => Graphs.SaveState(graph.Name, id, st));
        handle.GraphRunners[graph.Name] = runner;
        handle.Log($"graph '{graph.Name}' applied (states={graph.States.Count}, start={startState ?? graph.Initial}); {handle.GraphRunners.Count} graph(s) running");
        runner.Start();
        return runner;
    }

    /// <summary>Stop a bot's behaviour graph by <paramref name="graphName"/>, or ALL graphs if
    /// null. The bot stays in zone. Returns the number stopped.</summary>
    public int StopGraph(string id, string? graphName = null)
    {
        if (!_bots.TryGetValue(id, out var handle)) return 0;
        if (graphName is not null)
        {
            if (handle.GraphRunners.TryRemove(graphName, out var r)) { r.Dispose(); handle.Log($"graph '{graphName}' stopped"); return 1; }
            return 0;
        }
        var n = handle.GraphRunners.Count;
        foreach (var key in handle.GraphRunners.Keys.ToArray())
            if (handle.GraphRunners.TryRemove(key, out var r)) r.Dispose();
        if (n > 0) handle.Log($"stopped {n} graph(s)");
        return n;
    }

    /// <summary>Request a transition to <paramref name="state"/> in graph <paramref name="graphName"/>
    /// (or the only running graph if null). Operator flip / external request.</summary>
    public bool RequestState(string id, string state, string? graphName = null)
    {
        if (!_bots.TryGetValue(id, out var handle) || handle.GraphRunners.IsEmpty) return false;
        Scripting.BehaviorGraphRunner? r;
        if (graphName is not null) { if (!handle.GraphRunners.TryGetValue(graphName, out r)) return false; }
        else if (handle.GraphRunners.Count == 1) r = handle.GraphRunners.Values.First();
        else return false; // ambiguous: must name the graph when several run
        r.RequestState(state);
        return true;
    }

    /// <summary>Status of all behaviour graphs running on a bot (empty if none).</summary>
    public IReadOnlyList<ScriptStatus> GraphStatus(string id)
        => _bots.TryGetValue(id, out var handle) ? handle.GraphRunners.Values.Select(g => g.Status()).ToArray() : [];

    /// <summary>Outcome of a manual in-zone action.</summary>
    public enum ActionResult { Sent, NotFound, NotInZone }

    /// <summary>Make a bot say <paramref name="text"/> in its zone (local chat).</summary>
    public Task<ActionResult> SayAsync(string id, string text, CancellationToken ct = default)
        => ActAsync(id, $"say: \"{text}\"", s => s.SendAsync(ChatCodec.BuildChatReq(text), ct));

    /// <summary>Whisper <paramref name="text"/> to the player named <paramref name="to"/>.</summary>
    public Task<ActionResult> WhisperAsync(string id, string to, string text, CancellationToken ct = default)
        => ActAsync(id, $"whisper {to}: \"{text}\"", s => s.SendAsync(ChatCodec.BuildWhisperReq(to, text), ct));

    // The real client's cast sequence (from Z:/Buff.pcapng): TARGET the handle
    // (BAT TargettingReq), switch to battle/cast mode (ACT ChangemodeReq=2), THEN
    // send the skill cast (SKILLBASH_OBJ_CAST_REQ). Firing a bare cast with no
    // preceding target is rejected by the zone with a LinkendClientCmd kick. The
    // first two have no FiestaLib opcode attribute, so build them by opcode.
    private static readonly ushort OpBatTarget =
        (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.TargettingReq);
    private static readonly ushort OpActChangeMode =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.ChangemodeReq);

    /// <summary>Cast a skill on a target zone handle, replaying the client's
    /// target → battle-mode → (face/stop) → cast sequence so the zone accepts it.
    /// <paramref name="stopFirst"/> chooses whether to face+STOP before the cast:
    /// <list type="bullet">
    /// <item><c>true</c> → always face+STOP; <c>false</c> → never (cast while moving)</item>
    /// <item><c>null</c> (default) → <b>data-driven</b> from the skill's <c>ActiveSkill</c>
    /// row: face when <c>UsableDegree &gt; 0</c> (facing arc enforced) and/or STOP when it
    /// isn't a moving-skill. With no client data / unknown skill, falls back to the proven
    /// default (face+STOP) so a damage cast is never silently rejected.</item>
    /// </list>
    /// An offensive SKILLBASH is rejected (NC_BAT_SKILLBASH_CAST_FAIL_ACK) if the server
    /// considers us moving or out of the facing arc — verified in CombatExtensive.pcapng.
    /// A heal is castable while moving (IsMovingSkill), so the data path skips the STOP.</summary>
    public async Task<ActionResult> CastAsync(string id, ushort skill, ushort target, bool? stopFirst = null, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        // Record the cast attempt so the cast-fail reactive layer (CastFailed
        // subscriber) can retry the same skill+target.
        handle.LastCastSkill = skill;
        handle.LastCastTarget = target;
        var (needFace, needStop) = ResolveFaceStop(skill, stopFirst);
        await s.SendAsync(new FiestaPacket(OpBatTarget, new byte[] { (byte)target, (byte)(target >> 8) }), ct);
        await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);
        if ((needFace || needStop) && NpcPos(handle, target) is { } tp)
            await FaceAndStopAsync(handle, s, tp.X, tp.Y, ct);
        await s.SendAsync(new PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ { skill = skill, target = target }, ct);
        handle.Log(BotLogLevel.Verbose, $"cast skill {skill} on h={target} ({(needFace || needStop ? "target+mode+face+stop+cast" : "target+mode+cast")})");
        return ActionResult.Sent;
    }

    /// <summary>Decide whether a cast must face the target and/or STOP first. An explicit
    /// <paramref name="stopFirst"/> forces both on (<c>true</c>) or off (<c>false</c>);
    /// <c>null</c> reads the skill's <c>ActiveSkill</c> data (face if it enforces a facing
    /// arc, STOP if it isn't castable while moving), falling back to the proven face+STOP
    /// default when no client data is available so a damage cast is never silently rejected.
    /// FaceAndStopAsync turns + commits position in one step, so the two flags both just
    /// gate that call.</summary>
    private (bool NeedFace, bool NeedStop) ResolveFaceStop(ushort skill, bool? stopFirst)
    {
        if (stopFirst is { } force) return (force, force);
        if (ClientData?.Skill(skill) is { } si) return (si.UsableDegree > 0, !si.IsMovingSkill);
        return (true, true); // no data → proven default
    }

    /// <summary>Cast a <b>location-targeted</b> (ground / AoE) skill at a world coordinate —
    /// e.g. Frost Nova, which has a cast time and takes a target <i>point</i>, not a unit.
    /// Sends CHANGEMODE, then faces+STOPs toward the cast point when the skill needs it
    /// (data-driven from <c>ActiveSkill</c> by default — facing arc / moving-skill — same
    /// rules as <see cref="CastAsync"/>; <paramref name="stopFirst"/> overrides), then
    /// <c>NC_BAT_SKILLBASH_FLD_CAST_REQ {skill, locate}</c> — no target handle. The caster
    /// stays put and drops the AoE at (<paramref name="x"/>,<paramref name="y"/>), which
    /// may be up to the skill's Range away.</summary>
    public async Task<ActionResult> CastGroundAsync(string id, ushort skill, uint x, uint y, bool? stopFirst = null, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        handle.LastCastSkill = skill;
        handle.LastCastTarget = 0; // ground cast = no target handle
        var (needFace, needStop) = ResolveFaceStop(skill, stopFirst);
        await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);
        if (needFace || needStop) await FaceAndStopAsync(handle, s, x, y, ct);
        await s.SendAsync(new PROTO_NC_BAT_SKILLBASH_FLD_CAST_REQ { skill = skill, locate = new SHINE_XY_TYPE { x = x, y = y } }, ct);
        handle.Log(BotLogLevel.Verbose, $"ground-cast skill {skill} at ({x},{y})");
        return ActionResult.Sent;
    }

    /// <summary>Turn to face (<paramref name="tx"/>,<paramref name="ty"/>) and STOP there.
    /// There's no rotate packet — the client turns via MOVERUN, whose from→to vector sets
    /// facing — so this sends a tiny capped MOVERUN toward the point (just enough to turn,
    /// never closing a ranged caster into melee) then a STOP committing the position.
    /// Used before a cast that enforces UsableDegree and/or isn't a moving-skill. Advances
    /// the tracked position. No-op if the bot's position is unknown.</summary>
    private static async Task FaceAndStopAsync(BotHandle handle, BotSession s, uint tx, uint ty, CancellationToken ct)
    {
        if (handle.Position is not { } pos) return;
        var dx = (double)tx - pos.X; var dy = (double)ty - pos.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        uint faceX = pos.X, faceY = pos.Y;
        if (dist > 1)
        {
            var step = Math.Min(16.0, dist - 1); // enough to set facing; never overshoot
            faceX = (uint)Math.Round(pos.X + dx / dist * step);
            faceY = (uint)Math.Round(pos.Y + dy / dist * step);
        }
        var mv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(0), pos.X);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(4), pos.Y);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(8), faceX);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(12), faceY);
        await s.SendAsync(new FiestaPacket(OpMoveRun, mv), ct);
        var stop = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), faceX);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), faceY);
        await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
        handle.SetPosition(faceX, faceY);
    }

    /// <summary>Cast a heal skill on yourself (cast target = own handle). The client's
    /// "a heal lands on me even with an enemy targeted" is a client-side redirect, so
    /// the bot self-targets explicitly — otherwise the server heals the enemy/ally in
    /// the cast packet. Needs the self handle (from the [1802] login ack).</summary>
    public Task<ActionResult> HealSelfAsync(string id, ushort skill, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return Task.FromResult(ActionResult.NotFound);
        if (handle.SelfHandle is not { } self) return Task.FromResult(ActionResult.NotInZone);
        return CastAsync(id, skill, self, stopFirst: null, ct: ct); // data-driven: heal is a moving-skill
    }

    /// <summary>Attack: cast a (damage) skill on <paramref name="target"/>, or the
    /// nearest non-gate mob in view when target is 0. Bare target→mode→cast — it does
    /// NOT engage auto-attack or move the bot into melee (that would be wrong for a
    /// ranged/AoE caster; a mage casts without ever auto-attacking). To melee-swing,
    /// call <see cref="AutoAttackAsync"/> separately.</summary>
    public Task<ActionResult> AttackAsync(string id, ushort skill, ushort target = 0, CancellationToken ct = default)
    {
        if (target == 0 && _bots.TryGetValue(id, out var h) && NearestMob(h) is { } m) target = m;
        if (target == 0) return Task.FromResult(ActionResult.NotFound);
        return CastAsync(id, skill, target, ct: ct); // data-driven face/stop from ActiveSkill
    }

    // STOP (ACT StopReq 0x2012): 8 bytes [x u32][y u32] — the position the char halts at.
    private static readonly ushort OpActStop =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.StopReq);
    // ENDOFTRADE (ACT cmd 11 = 0x200B, empty): closes the current NPC shop/trade interaction. Must be
    // sent before opening a DIFFERENT NPC's shop (the server won't open a 2nd shop while one is open).
    private const ushort OpActEndOfTrade = (ushort)(((int)ProtocolCommand.Act << 10) | 11);
    // BASHSTART / BASHSTOP (BAT 0x242B / 0x2432, empty): begin / end melee auto-attack on
    // the current target. While bashing the server streams SWING_START/SWING_DAMAGE
    // (0x2447/0x2448) until the mob dies or we BASHSTOP — verified in CombatExtensive.pcapng.
    private const ushort OpBatBashStart = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.BashstartCmd);
    private const ushort OpBatBashStop = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.BashstopCmd);

    /// <summary>Begin auto-attacking (melee swings) a target, or the nearest mob if 0.
    /// Mirrors the real client (CombatExtensive.pcapng, "click once, many swings"):
    /// target → battle mode → a short MOVERUN toward the mob + STOP (close to melee
    /// range and FACE it — a swing is rejected otherwise) → BASHSTART, after which the
    /// server streams continuous swing/damage until the mob dies or
    /// <see cref="StopAttackAsync"/>. Auto-attack is inherently melee; skills are cast
    /// separately via <see cref="AttackAsync"/>/<see cref="CastAsync"/> and are NOT
    /// coupled to this (a mage never auto-attacks).</summary>
    public async Task<ActionResult> AutoAttackAsync(string id, ushort target = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        if (target == 0 && NearestMob(handle) is { } m) target = m;
        if (target == 0) return ActionResult.NotFound;

        await s.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)target, (byte)(target >> 8) }), ct);
        await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);

        // FACE the mob then STOP, exactly like a skill cast (CastAsync) — the server validates
        // a swing against facing, and a zero-distance moverun (when already adjacent) does NOT
        // set facing, which is why BASHSTART produced no swings. FaceAndStop always steps a
        // little toward the target so facing is correct. The CALLER must already be within
        // melee weapon range (walk there first); this doesn't close large gaps.
        if (NpcPos(handle, target) is { } tp)
            await FaceAndStopAsync(handle, s, tp.X, tp.Y, ct);
        await s.SendAsync(new FiestaPacket(OpBatBashStart, Array.Empty<byte>()), ct);
        handle.Log(BotLogLevel.Verbose, $"auto-attack h={target} (faced+bashstart)");
        return ActionResult.Sent;
    }

    /// <summary>Stop auto-attacking (BAT BashstopCmd).</summary>
    public Task<ActionResult> StopAttackAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "stop auto-attack", s => s.SendAsync(new FiestaPacket(OpBatBashStop, Array.Empty<byte>()), ct));

    /// <summary>Position of a nearby NPC/mob by zone handle (null if not in view).</summary>
    private static (uint X, uint Y)? NpcPos(BotHandle handle, ushort target)
    {
        if (handle.ZoneView is not { } view) return null;
        foreach (var n in view.NearbyNpcs) if (n.Handle == target) return (n.X, n.Y);
        return null;
    }

    /// <summary>Handle of the nearest huntable enemy to the bot (null if none in view).
    /// Filters out gates, town guards (player-side), shop NPCs, and gatherable resource
    /// nodes via client MobInfo (<see cref="GameData.ClientData.IsHuntableEnemy"/>) — so
    /// auto-attack never charges a guard or a herb. Pass an explicit target to
    /// <see cref="AttackAsync"/> to override the filter.</summary>
    private ushort? NearestMob(BotHandle handle)
    {
        if (handle.ZoneView is not { } view || handle.Position is not { } pos) return null;
        ushort? best = null; var bestD = double.MaxValue;
        foreach (var n in view.NearbyNpcs)
        {
            if (n.IsGate) continue;
            if (ClientData is { } cd && !cd.IsHuntableEnemy(n.MobId)) continue; // skip guards/NPCs/resources
            var d = Math.Pow((double)n.X - pos.X, 2) + Math.Pow((double)n.Y - pos.Y, 2);
            if (d < bestD) { bestD = d; best = n.Handle; }
        }
        return best;
    }

    /// <summary>Number of mobs currently aggroing the bot (within the combat window) — the
    /// "am I overwhelmed?" signal a survival script flees on.</summary>
    public int AggressorCount(string id)
        => _bots.TryGetValue(id, out var h) && h.ZoneView is { } v ? v.Aggressors.Count : 0;

    /// <summary>Flee: walk directly away from the threat (centroid of current aggressors, or
    /// the nearest mob) by <paramref name="dist"/> world-units. NON-BLOCKING — it just issues
    /// the walk and returns, so a survival script can keep healing every tick while fleeing.
    /// Pathfinds over the map grid when available, else a straight retreat.</summary>
    public ActionResult Flee(string id, double dist = 500, double unitsPerSec = 0)
    {
        if (!_bots.TryGetValue(id, out var h)) return ActionResult.NotFound;
        if (h.Phase != BotPhase.InZone || h.ZoneSession is null) return ActionResult.NotInZone;
        if (h.ZoneView is not { } v || h.Position is not { } pos) return ActionResult.NotInZone;

        double cx = 0, cy = 0; int n = 0;
        foreach (var ag in v.Aggressors) if (NpcPos(h, ag) is { } p) { cx += p.X; cy += p.Y; n++; }
        if (n == 0 && NearestMob(h) is { } m && NpcPos(h, m) is { } mp) { cx = mp.X; cy = mp.Y; n = 1; }
        if (n == 0) return ActionResult.NotFound; // nothing to flee from
        cx /= n; cy /= n;

        double dx = pos.X - cx, dy = pos.Y - cy;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) { dx = 1; dy = 0; len = 1; }
        var tx = (uint)Math.Max(0, pos.X + dx / len * dist);
        var ty = (uint)Math.Max(0, pos.Y + dy / len * dist);

        var grid = h.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
        IReadOnlyList<(uint X, uint Y)> wp;
        if (grid is not null && PathFinder.FindPath(grid, pos.X, pos.Y, tx, ty) is { Count: > 0 } path)
            wp = PathFinder.Simplify(path);
        else wp = new[] { (pos.X, pos.Y), (tx, ty) };
        return WalkPath(id, wp, unitsPerSec > 0 ? unitsPerSec : 120.0);
    }

    // ── Targeting / follow (zone) ─────────────────────────────────────────────
    // Targeting and follow are zone-side. A party-tab target (F2–F5 in the client)
    // is just a BAT TargettingReq on the member's zone handle; untarget (Esc) is a
    // bare BAT UntargetReq. "Follow" has no dedicated packet at all — the client
    // targets the player then streams MoverunCmd toward their moving position, which
    // is why follow drops at a map change (verified in PartyFriendTarget.pcapng).
    private static readonly ushort OpBatUntarget =
        (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.UntargetReq);

    /// <summary>Target a zone handle (e.g. a party member for buffing).</summary>
    public Task<ActionResult> TargetAsync(string id, ushort target, CancellationToken ct = default)
        => ActAsync(id, $"target h={target}",
            s => s.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)target, (byte)(target >> 8) }), ct));

    /// <summary>Clear the current target (Esc).</summary>
    public Task<ActionResult> UntargetAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "untarget", s => s.SendAsync(new FiestaPacket(OpBatUntarget, Array.Empty<byte>()), ct));

    /// <summary>Resolve a nearby player by name (case-insensitive) to their zone handle.</summary>
    private static ushort? HandleForName(BotHandle handle, string name)
        => handle.ZoneView?.NearbyPlayers
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Handle;

    /// <summary>Follow a nearby player by name: target them, then chase by streaming
    /// MoverunCmd toward their live position (stopping <paramref name="followDist"/>
    /// world-units short), refreshed on a background loop until the target leaves
    /// view, the bot is stopped, or <see cref="StopFollow"/> is called. This mirrors
    /// the real client — there is no follow packet, so a map change ends the follow.</summary>
    public ActionResult Follow(string id, string targetName, double followDist = 60.0, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session) return ActionResult.NotInZone;
        if (HandleForName(handle, targetName) is not { } h0) return ActionResult.NotFound;

        var followCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.FollowCts?.Cancel();
        handle.FollowCts = followCts;
        var ct = followCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)h0, (byte)(h0 >> 8) }), ct);
                handle.Log($"follow: chasing {targetName} (h={h0})");
                (uint X, uint Y)? lastPlan = null;
                while (!ct.IsCancellationRequested)
                {
                    var target = handle.ZoneView?.NearbyPlayers
                        .FirstOrDefault(p => string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase));
                    if (target is null) { handle.Log($"follow: {targetName} left view — stopping"); break; }
                    if (handle.Position is { } pos)
                    {
                        double dx = (double)target.X - pos.X, dy = (double)target.Y - pos.Y;
                        var dist = Math.Sqrt(dx * dx + dy * dy);
                        // Re-plan when out of range and either the target moved enough
                        // since the last plan, or our last walk finished short (WalkCts
                        // cleared) — the latter closes the final gap when they stop.
                        var moved = lastPlan is not { } lp
                            || Math.Abs((double)target.X - lp.X) + Math.Abs((double)target.Y - lp.Y) > followDist
                            || handle.WalkCts is null;
                        if (dist > followDist && moved)
                        {
                            var grid = handle.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
                            if (grid is not null)
                            {
                                // Pathfind around obstacles (a straight chase snags on
                                // lanterns/walls → MOVEFAIL), then walk it via WalkPath
                                // (chunked + MOVEFAIL-aware), trimmed to stop short.
                                var path = PathFinder.FindPath(grid, pos.X, pos.Y, target.X, target.Y);
                                if (path.Count > 0)
                                {
                                    var wp = PathFinder.Simplify(path);
                                    int keep = wp.Count;
                                    while (keep > 1)
                                    {
                                        var (wx, wy) = wp[keep - 1];
                                        if (Math.Sqrt(Math.Pow((double)wx - target.X, 2) + Math.Pow((double)wy - target.Y, 2)) < followDist) keep--;
                                        else break;
                                    }
                                    if (keep >= 2) WalkPath(id, wp.Take(keep).ToList(), unitsPerSec);
                                    lastPlan = (target.X, target.Y);
                                }
                            }
                            else
                            {
                                // No grid available — one capped straight-line step.
                                var step = Math.Min(dist - followDist, MaxStepFor(unitsPerSec));
                                var nx = (uint)Math.Round(pos.X + dx / dist * step);
                                var ny = (uint)Math.Round(pos.Y + dy / dist * step);
                                var p = new byte[16];
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), pos.X);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), pos.Y);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), nx);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), ny);
                                await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                                handle.SetPosition(nx, ny);
                            }
                        }
                    }
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { handle.Log("follow: stopped"); }
            catch (Exception ex) { handle.Log($"follow error: {ex.Message}"); }
            finally { if (ReferenceEquals(handle.FollowCts, followCts)) handle.FollowCts = null; followCts.Dispose(); }
        }, ct);
        return ActionResult.Sent;
    }

    /// <summary>Stop an in-progress <see cref="Follow"/> loop (no-op if not following).</summary>
    public ActionResult StopFollow(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        handle.FollowCts?.Cancel();
        return ActionResult.Sent;
    }

    // ── Party (WorldManager link) ─────────────────────────────────────────────
    // Party and friend traffic is WM-side, not zone-side (verified in
    // PartyFriendTarget.pcapng). An invite is NC_PARTY_JOIN_REQ {target Name5}; the
    // recipient answers a join-propose with ALLOW_ACK / REJECT_ACK carrying the
    // *inviter's* name (no typed struct for those two — build by opcode). Letting an
    // invite expire = simply not answering.
    private const ushort OpPartyAllow = (ushort)(((int)ProtocolCommand.Party << 10) | 4); // 0x3804
    private const ushort OpPartyReject = (ushort)(((int)ProtocolCommand.Party << 10) | 5); // 0x3805
    // Incoming invite: the server asks the invitee with NC_PARTY_JOINPROPOSE_REQ (cmd 3,
    // 0x3803) carrying the inviter's name. We track it so accept/decline don't need the
    // name passed in (and so an invite doesn't sit unanswered, wedging the party state).
    private const ushort OpPartyJoinPropose = (ushort)(((int)ProtocolCommand.Party << 10) | 3); // 0x3803
    private const ushort OpPartyJoinCmd = (ushort)(((int)ProtocolCommand.Party << 10) | 8); // 0x3808 (joined)
    // Incoming friend request: the server asks the bot to confirm with
    // NC_FRIEND_SET_CONFIRM_REQ (Friend dept 0x15, cmd 3 → 0x5403) carrying the requester's
    // name (charid). We track it so the bot can auto-confirm (an operator friends the bot and
    // it accepts on its own). NC_FRIEND_ADD_CMD (cmd 8) means the add went through → clear.
    private const ushort OpFriendConfirmReq = (ushort)(((int)ProtocolCommand.Friend << 10) | 3); // 0x5403
    private const ushort OpFriendAddCmd = (ushort)(((int)ProtocolCommand.Friend << 10) | 8); // 0x5408

    /// <summary>Subscribe a BotHandle's WM link to track pending party invites + incoming
    /// friend requests (and clear them when resolved). Called when the WM session is created.</summary>
    private void TrackPartyInvites(BotHandle handle, BotSession wm)
        => wm.PacketReceived += pkt =>
        {
            try
            {
                if (pkt.Opcode == OpPartyJoinPropose)
                {
                    var inviter = FiestaText.Decode(pkt.ReadBody<PROTO_NC_PARTY_JOINPROPOSE_REQ>().mastername.n5_name);
                    handle.PendingPartyInviter = inviter;
                    handle.Log($"party invite from '{inviter}' pending — acceptParty/declineParty to answer");
                }
                else if (pkt.Opcode == OpPartyJoinCmd) handle.PendingPartyInviter = null; // joined; invite resolved
                else if (pkt.Opcode == OpFriendConfirmReq)
                {
                    // charid = the requester (the player adding the bot); friendid = the bot.
                    var requester = FiestaText.Decode(pkt.ReadBody<PROTO_NC_FRIEND_SET_CONFIRM_REQ>().charid.n5_name);
                    handle.PendingFriendRequester = requester;
                    handle.Log($"friend request from '{requester}' pending — friendConfirm to answer");
                }
                else if (pkt.Opcode == OpFriendAddCmd) handle.PendingFriendRequester = null; // added; resolved
            }
            catch { /* ignore an unparseable WM frame */ }
        };

    /// <summary>Invite <paramref name="targetName"/> to a party (WM link).</summary>
    public Task<ActionResult> PartyInviteAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"party invite {targetName}",
            s => s.SendAsync(new PROTO_NC_PARTY_JOIN_REQ { target = Name5Of(targetName) }, ct));

    /// <summary>Accept a pending party invite. Pass <paramref name="inviterName"/> explicitly,
    /// or leave it null to answer the tracked <see cref="BotHandle.PendingPartyInviter"/>.</summary>
    public Task<ActionResult> PartyAcceptAsync(string id, string? inviterName = null, CancellationToken ct = default)
    {
        var name = ResolveInviter(id, inviterName);
        if (name is null) return Task.FromResult(ActionResult.NotFound);
        return WmActAsync(id, $"party accept {name}",
            s => s.SendAsync(new FiestaPacket(OpPartyAllow, Name5Of(name).n5_name), ct))
            .ContinueWith(t => { if (_bots.TryGetValue(id, out var h)) h.PendingPartyInviter = null; return t.Result; });
    }

    /// <summary>Decline a pending party invite (tracked inviter if <paramref name="inviterName"/>
    /// is null) — clears the stuck pending state.</summary>
    public Task<ActionResult> PartyDeclineAsync(string id, string? inviterName = null, CancellationToken ct = default)
    {
        var name = ResolveInviter(id, inviterName);
        if (name is null) return Task.FromResult(ActionResult.NotFound);
        return WmActAsync(id, $"party decline {name}",
            s => s.SendAsync(new FiestaPacket(OpPartyReject, Name5Of(name).n5_name), ct))
            .ContinueWith(t => { if (_bots.TryGetValue(id, out var h)) h.PendingPartyInviter = null; return t.Result; });
    }

    /// <summary>The explicit inviter name, or the tracked pending one if none was given.</summary>
    private string? ResolveInviter(string id, string? inviterName)
    {
        if (!string.IsNullOrWhiteSpace(inviterName)) return inviterName;
        return _bots.TryGetValue(id, out var h) && !string.IsNullOrWhiteSpace(h.PendingPartyInviter)
            ? h.PendingPartyInviter : null;
    }

    /// <summary>Send a line to party chat (WM link).</summary>
    public Task<ActionResult> PartyChatAsync(string id, string text, CancellationToken ct = default)
        => WmActAsync(id, $"party-chat: \"{text}\"", s => s.SendAsync(ChatCodec.BuildPartyChatReq(text), ct));

    // ── Friend list (WorldManager link) ───────────────────────────────────────
    // All friend structs carry [self charid Name5][other friendid Name5]; the confirm
    // adds a trailing accept byte (0x01 accept / 0x00 decline). Add and delete are
    // one-shot requests; responding to an incoming request is the CONFIRM_ACK.

    /// <summary>Send a friend request to <paramref name="targetName"/> (WM link).</summary>
    public Task<ActionResult> FriendAddAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"friend add {targetName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_SET_REQ { charid = Name5Of(self), friendid = Name5Of(targetName) }, ct));

    /// <summary>Answer an incoming friend request from <paramref name="requesterName"/>:
    /// <paramref name="accept"/> true = add, false = decline (WM link).</summary>
    public Task<ActionResult> FriendConfirmAsync(string id, string requesterName, bool accept, CancellationToken ct = default)
    {
        if (_bots.TryGetValue(id, out var h) && string.Equals(h.PendingFriendRequester, requesterName, StringComparison.OrdinalIgnoreCase))
            h.PendingFriendRequester = null; // answered — clear so a social script won't re-confirm
        return WmActAsync(id, $"friend {(accept ? "accept" : "decline")} {requesterName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_SET_CONFIRM_ACK
            {
                charid = Name5Of(self), friendid = Name5Of(requesterName), accept_friend = (byte)(accept ? 1 : 0)
            }, ct));
    }

    /// <summary>Remove <paramref name="targetName"/> from the friend list (WM link).</summary>
    public Task<ActionResult> FriendDeleteAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"friend delete {targetName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_DEL_REQ { charid = Name5Of(self), friendid = Name5Of(targetName) }, ct));

    /// <summary>Build a 20-byte Name5 from a character name (ASCII, NUL-padded).</summary>
    private static Name5 Name5Of(string name)
    {
        var n5 = new Name5();
        var b = FiestaText.Encode(name);
        Array.Copy(b, n5.n5_name, Math.Min(b.Length, n5.n5_name.Length));
        return n5;
    }

    // Town multi-select portal (from Portals.pcapng): target the portal NPC, click
    // it, then select a destination by its TownPortal-table index. Built by opcode —
    // NPCCLICK (Act cmd 10) and TOWNPORTAL_REQ (Map cmd 26) carry trivial payloads.
    private const ushort OpActNpcClick = (ushort)(((int)ProtocolCommand.Act << 10) | 10);     // 0x200A
    private const ushort OpMapTownPortal = (ushort)(((int)ProtocolCommand.Map << 10) | 26);   // 0x181A

    /// <summary>Use a town multi-select portal: target → click the portal NPC →
    /// select destination <paramref name="dest"/> (its <c>TownPortal</c> table index;
    /// e.g. in RouN group 0: 0=RouN,1=RouVal01,2=Eld). The bot must already be next
    /// to the portal NPC. The server then map-transitions the bot.</summary>
    public Task<ActionResult> TownPortalAsync(string id, ushort npcHandle, byte dest, CancellationToken ct = default)
        => ActAsync(id, $"town-portal via npc h={npcHandle} -> dest {dest}", async s =>
        {
            var hb = new byte[] { (byte)npcHandle, (byte)(npcHandle >> 8) };
            await s.SendAsync(new FiestaPacket(OpBatTarget, hb), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            await s.SendAsync(new FiestaPacket(OpMapTownPortal, new[] { dest }), ct);
        });

    // Field-gate link: a gate is an NPC (flagstate=1) with a handle. The client takes
    // it the same way it clicks any NPC — target it then NPCClick — and the zone
    // replies with the LOGOUT + LINKSAME/LINKOTHER transition; no need to walk onto
    // the tile (just be within the gate's range). For a multi-destination gate the
    // server first sends MULTY_LINK_CMD and the client picks a destination by map
    // name via MULTY_LINK_SELECT_REQ. (Verified C->S in Portals.pcapng, 2026-06-11.)
    private const ushort OpMapMultyLinkSelect = (ushort)(((int)ProtocolCommand.Map << 10) | 31); // 0x181F
    // SERVERMENU_ACK (Menu dept 0x0F, cmd 2): answers a server menu prompt (0x3C01).
    // An instance gate (EldPri01) asks "move to Collapsed Prison field?" — option 0 =
    // Yes (verified C->S in PartyFriendTarget.pcapng).
    private const ushort OpMenuServerMenuAck = (ushort)((0x0F << 10) | 2); // 0x3C02
    // MAP_LOGINCOMPLETE (Map dept 6, cmd 3): "finished loading — spawn me in-world".
    // Sent at initial zone entry (ZoneEntry) and AGAIN after an in-band LINKSAME warp:
    // the server holds back the new map's entity broadcasts until it sees this, so an
    // instance (EldPri01) stays silent/empty without it.
    private const ushort OpMapLoginComplete = (ushort)(((int)ProtocolCommand.Map << 10) | 3); // 0x1803

    /// <summary>Take a field gate by its NPC handle: target → NPC-click. If the gate
    /// opens a Yes/No confirm menu (0x3C01, e.g. instance gates like EldPri01), answer
    /// it with SERVERMENU_ACK option <paramref name="menuOption"/> (0 = Yes). For a
    /// multi-destination gate, pass <paramref name="destMap"/> to pick the map by name.
    /// The bot must be within the gate's range; the zone then drives the transition
    /// (see <see cref="ZoneView.MapChanged"/>).</summary>
    public async Task<ActionResult> UseGateAsync(string id, ushort gateHandle, string? destMap = null, byte menuOption = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        var hb = new byte[] { (byte)gateHandle, (byte)(gateHandle >> 8) };
        var view = handle.ZoneView;

        // An instance gate confirms with a menu (0x3C01 "move to <field>?") before it
        // transitions you; a plain field gate transitions outright with no menu. The
        // gate also auto-opens that menu when you stand on its trigger — so if we
        // spawned on the gate the menu may already be open before we click. Answer an
        // already-open menu directly; otherwise target+click and poll ~3s for one.
        async Task AnswerMenu()
        {
            // Capture the parsed menu BEFORE answering (ClearServerMenu wipes it) so the trace shows
            // exactly what we picked and from what prompt — wrong picks (e.g. landing in a shop instead
            // of a quest) are then obvious in the tail.
            var title = view?.ServerMenuTitle;
            var picked = view?.ServerMenuOptions.FirstOrDefault(o => o.Reply == menuOption);
            await s.SendAsync(new FiestaPacket(OpMenuServerMenuAck, new[] { menuOption }), ct);
            view?.ClearServerMenu();
            handle.Log($"server menu answered: reply={menuOption} ([{menuOption}]={picked?.Text ?? "?"}) for \"{title ?? "?"}\"");
        }

        if (view?.ServerMenuOpen == true)
        {
            await AnswerMenu();
        }
        else
        {
            await s.SendAsync(new FiestaPacket(OpBatTarget, hb), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            for (var waited = 0; waited < 3000; waited += 150)
            {
                await Task.Delay(150, ct);
                if (view?.ServerMenuOpen == true) { await AnswerMenu(); break; }
            }
        }
        if (!string.IsNullOrWhiteSpace(destMap))
        {
            var name3 = new byte[12]; // Name3, ASCII, null-padded
            var bytes = System.Text.Encoding.ASCII.GetBytes(destMap);
            Array.Copy(bytes, name3, Math.Min(bytes.Length, name3.Length));
            await s.SendAsync(new FiestaPacket(OpMapMultyLinkSelect, name3), ct);
        }
        handle.Log($"use gate h={gateHandle}{(destMap is null ? "" : $" -> {destMap}")}");
        return ActionResult.Sent;
    }

    /// <summary>Snapshot the gates the bot currently sees into the shared
    /// <see cref="Graph"/> (auto-discovery): each in-view gate becomes an edge from
    /// the bot's current map to the gate's destination. No-op until the bot knows its
    /// current map and is in zone. Returns the number of gate edges observed.</summary>
    public int ObserveGates(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return 0;
        if (handle.CurrentMap is not { } fromMap || handle.ZoneView is not { } view) return 0;
        var n = 0;
        foreach (var gate in view.NearbyNpcs)
        {
            if (!gate.IsGate || string.IsNullOrWhiteSpace(gate.LinkMap)) continue;
            Graph.ObserveGate(fromMap, gate.LinkMap!, gate.X, gate.Y, gate.Handle);
            n++;
        }
        return n;
    }

    // ── Autonomous multi-map travel ───────────────────────────────────────────
    // Stop this far (world units) short of a gate before clicking it — a gate takes
    // effect within its range, no need to stand on the tile (verified Portals.pcapng).
    // If a click from range doesn't transition, we close to the exact coord and retry.
    private const double GateApproachDist = 60.0;

    /// <summary>Outcome of kicking off an autonomous <see cref="TravelTo"/>.</summary>
    public enum TravelResult { Started, NotFound, NotInZone, AlreadyThere, NoRoute }

    /// <summary>Plan and begin autonomous travel to <paramref name="destMap"/>: route
    /// over the learned gate graph (BFS), then for each hop pathfind to the gate, take
    /// it, wait for the map transition (in-band LINKSAME or cross-server LINKOTHER), and
    /// repeat — learning each map's id↔name and gates as it goes. Returns immediately
    /// with the planned route; the journey runs on a background task (watch the bot log
    /// and <see cref="BotHandle.CurrentMap"/>). Cancel with <see cref="StopTravel"/>.</summary>
    public (TravelResult Result, IReadOnlyList<GateEdge>? Route) TravelTo(string id, string destMap, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return (TravelResult.NotFound, null);
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is null) return (TravelResult.NotInZone, null);
        if (handle.CurrentMap is not { } from) return (TravelResult.NotInZone, null);
        if (string.Equals(from, destMap, StringComparison.OrdinalIgnoreCase))
            return (TravelResult.AlreadyThere, Array.Empty<GateEdge>());

        // Seed the COMPLETE cross-map web from client nav data once (MapWayPoint/MapLinkPoint), so
        // routing works to maps the bot has never visited — not just auto-discovered links.
        if (!Graph.Seeded && ClientData is { } cd)
        {
            var seedEdges = cd.BuildGateEdges();
            Graph.Seed(seedEdges);
            _bots.TryGetValue(id, out var hh); hh?.Log($"[travel] seeded map graph: {seedEdges.Count} cross-map gate edges");
        }
        ObserveGates(id); // fold the bot's in-view gates into the graph (refreshes live handles)
        var route = Graph.Route(from, destMap);
        if (route is null || route.Count == 0) return (TravelResult.NoRoute, null);

        var travelCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.TravelCts?.Cancel();
        handle.TravelCts = travelCts;
        _ = Task.Run(() => RunTravelAsync(handle, route, unitsPerSec, travelCts), travelCts.Token);
        return (TravelResult.Started, route);
    }

    /// <summary>Stop an in-progress <see cref="TravelTo"/> (no-op if not travelling).
    /// Also aborts the current walk so the bot halts where it is.</summary>
    public ActionResult StopTravel(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        handle.TravelCts?.Cancel();
        handle.WalkCts?.Cancel();
        return ActionResult.Sent;
    }

    private async Task RunTravelAsync(BotHandle handle, IReadOnlyList<GateEdge> route, double unitsPerSec, CancellationTokenSource travelCts)
    {
        var id = handle.Id;
        var ct = travelCts.Token;
        try
        {
            handle.Log($"[travel] start -> {route[^1].ToMap} via {route.Count} hop(s): " +
                       string.Join(" -> ", route.Select(e => e.ToMap)));
            for (int hop = 0; hop < route.Count; hop++)
            {
                if (ct.IsCancellationRequested) break;
                var edge = route[hop];
                var expected = edge.ToMap;

                // Walk to the gate's KNOWN location first (seeded from MapWayPoint/MapLinkPoint, or
                // last observed). A map's gate can be anywhere on it; the old code only took a gate
                // already in view and aborted otherwise — the #1 cause of "pathfinding failed". By
                // approaching the stored coords the gate NPC comes into view, then we resolve + click.
                if (edge.GateX != 0 || edge.GateY != 0)
                    await ApproachAsync(id, handle, edge.GateX, edge.GateY, GateApproachDist, unitsPerSec, ct);

                // Resolve the live gate to this destination from the current view (now that we're at
                // its location). Gates arrive in the field MOB briefinfo — wait briefly for it.
                if (!await WaitUntilAsync(() => GateTo(handle, expected) is not null, 8000, ct)
                    || GateTo(handle, expected) is not { } gate)
                {
                    handle.Log($"[travel] hop {hop + 1}/{route.Count}: no gate to '{expected}' in view near ({edge.GateX},{edge.GateY}) — aborting");
                    return;
                }
                handle.Log($"[travel] hop {hop + 1}/{route.Count}: -> {expected} via gate h={gate.Handle} @({gate.X},{gate.Y})");

                // Walk to within range of the gate (pathfind around obstacles if a grid
                // is available; else a best-effort straight approach via WalkPath).
                await ApproachAsync(id, handle, gate.X, gate.Y, GateApproachDist, unitsPerSec, ct);

                // Take the gate and wait for the transition. Tell OnMapChanged the
                // destination we're heading into (PendingDestMap) so it resolves + learns
                // the real map name from the handoff's id deterministically — before the
                // cross-server reconnect re-reads it. If clicking from range doesn't fire
                // the gate, close onto the exact tile and retry once.
                handle.PendingDestMap = expected;
                var seqBefore = handle.MapChangeSeq;
                await UseGateAsync(id, gate.Handle, ct: ct);
                if (!await WaitUntilAsync(() => handle.MapChangeSeq > seqBefore, 6000, ct))
                {
                    handle.Log($"[travel] hop {hop + 1}: gate didn't fire from range — closing in and retrying");
                    await ApproachAsync(id, handle, gate.X, gate.Y, 0, unitsPerSec, ct);
                    await UseGateAsync(id, gate.Handle, ct: ct);
                    if (!await WaitUntilAsync(() => handle.MapChangeSeq > seqBefore, 8000, ct))
                    {
                        handle.Log($"[travel] hop {hop + 1}: no transition after retry — aborting");
                        handle.PendingDestMap = null;
                        return;
                    }
                }
                handle.PendingDestMap = null; // consumed by OnMapChanged

                // A cross-server hop re-logs in on a fresh connection — wait until we're
                // back in zone before the next hop. In-band LINKSAME stays InZone throughout.
                if (!await WaitUntilAsync(
                        () => handle.Phase == BotPhase.InZone && handle.ZoneSession is not null, 20000, ct))
                {
                    handle.Log($"[travel] hop {hop + 1}: didn't re-enter zone after handoff — aborting");
                    return;
                }
                handle.SetCurrentMap(expected); // belt-and-suspenders for the next hop's grid
                ObserveGates(id); // learn the new map's gates (next hop + future routing)
                handle.Log($"[travel] hop {hop + 1}/{route.Count}: arrived on {expected}");
            }
            handle.Log(ct.IsCancellationRequested ? "[travel] cancelled" : $"[travel] done — arrived on {route[^1].ToMap}");
        }
        catch (OperationCanceledException) { handle.Log("[travel] cancelled"); }
        catch (Exception ex) { handle.Log($"[travel] error: {ex.Message}"); }
        finally { if (ReferenceEquals(handle.TravelCts, travelCts)) handle.TravelCts = null; travelCts.Dispose(); }
    }

    /// <summary>The live in-view gate whose link destination is <paramref name="map"/>
    /// (case-insensitive), or null if none is currently visible.</summary>
    private static NearbyNpc? GateTo(BotHandle handle, string map)
        => handle.ZoneView?.NearbyNpcs.FirstOrDefault(
            n => n.IsGate && string.Equals(n.LinkMap, map, StringComparison.OrdinalIgnoreCase));

    /// <summary>Walk the bot to within <paramref name="stopShort"/> world-units of
    /// (<paramref name="tx"/>,<paramref name="ty"/>), pathfinding over the current map's
    /// grid when one is available (a straight WalkPath segment otherwise), then wait
    /// until it arrives, the walk ends, or a 30 s cap. Used to reach a gate before
    /// taking it; shared by the travel loop.</summary>
    private async Task ApproachAsync(string id, BotHandle handle, uint tx, uint ty, double stopShort, double unitsPerSec, CancellationToken ct)
    {
        if (handle.Position is not { } pos) return;
        var grid = handle.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
        IReadOnlyList<(uint X, uint Y)> wp;
        if (grid is not null)
        {
            var path = PathFinder.FindPath(grid, pos.X, pos.Y, tx, ty);
            wp = path.Count == 0
                ? new[] { (pos.X, pos.Y), (tx, ty) } // unreachable on the grid — try direct
                : PathFinder.Simplify(path);
        }
        else wp = new[] { (pos.X, pos.Y), (tx, ty) };

        // Trim trailing waypoints inside stopShort of the target so we halt short of the gate.
        if (stopShort > 0 && wp.Count > 2)
        {
            var keep = wp.Count;
            while (keep > 2 && Dist(wp[keep - 1], tx, ty) < stopShort) keep--;
            wp = wp.Take(keep).ToList();
        }
        WalkPath(id, wp, unitsPerSec);
        await WaitUntilAsync(
            () => (handle.Position is { } p && Dist((p.X, p.Y), tx, ty) <= Math.Max(stopShort, 24)) || handle.WalkCts is null,
            30000, ct);
    }

    private static double Dist((uint X, uint Y) a, uint x, uint y)
        => Math.Sqrt(Math.Pow((double)a.X - x, 2) + Math.Pow((double)a.Y - y, 2));

    /// <summary>Poll <paramref name="cond"/> until it's true or <paramref name="timeoutMs"/>
    /// elapses; returns the final state. Throws if <paramref name="ct"/> is cancelled.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> cond, int timeoutMs, CancellationToken ct, int pollMs = 150)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(pollMs, ct);
        }
        return cond();
    }

    // Soul-stone HP/SP recharge (SOULSTONE dept 20: HP_USE_REQ cmd 7 = 0x5007, SP_USE_REQ
    // cmd 9 = 0x5009; both empty payload). The in-game "use an HP/SP stone" — draws HP/SP
    // from the character's soul-stone reserve into the current pool. Its OWN packet, NOT an
    // item use. Verified C->S in CombatExtensive.pcapng (chat "I will use an SP stone" →
    // 0x5009). Server replies USESUC_ACK (0x5008/0x500A) on success or USEFAIL_ACK (0x5006).
    private const ushort OpSoulStoneHpUse = 0x5007;
    private const ushort OpSoulStoneSpUse = 0x5009;

    /// <summary>Recharge current SP from the character's SP soul-stone reserve
    /// (NC_SOULSTONE_SP_USE_REQ) — the in-game "use an SP stone". Needed before a costly
    /// cast when current SP is low (the reserve must be charged).</summary>
    public Task<ActionResult> UseSoulStoneSpAsync(string id, CancellationToken ct = default)
    {
        // Don't USE at full SP — the server rejects it (USEFAIL), wasting the call and (worse)
        // looking like an empty reserve. Skip as a no-op when already full.
        if (_bots.TryGetValue(id, out var h) && h.ZoneView is { } v && v.Sp is { } sp && v.MaxSp > 0 && sp >= v.MaxSp)
            return Task.FromResult(ActionResult.Sent);
        return ActAsync(id, "soul-stone SP recharge (0x5009)",
            s => s.SendAsync(new FiestaPacket(OpSoulStoneSpUse, ReadOnlyMemory<byte>.Empty), ct));
    }

    /// <summary>Recharge current HP from the character's HP soul-stone reserve
    /// (NC_SOULSTONE_HP_USE_REQ) — the in-game "use an HP stone". The combat-survival
    /// analogue of <see cref="UseSoulStoneSpAsync"/>; an instant out-of-combat-free heal
    /// from the reserve.</summary>
    public Task<ActionResult> UseSoulStoneHpAsync(string id, CancellationToken ct = default)
    {
        // Don't USE at full HP — the server rejects it (USEFAIL at 100% HP), which both wastes the
        // call and falsely reads as an empty reserve. Skip as a no-op when already full.
        if (_bots.TryGetValue(id, out var h) && h.ZoneView is { } v && v.Hp is { } hp && v.MaxHp > 0 && hp >= v.MaxHp)
            return Task.FromResult(ActionResult.Sent);
        return ActAsync(id, "soul-stone HP recharge (0x5007)",
            s => s.SendAsync(new FiestaPacket(OpSoulStoneHpUse, ReadOnlyMemory<byte>.Empty), ct));
    }

    // Shop / buy. Clicking a merchant (target → NPCClick) makes the server send the
    // SHOPOPEN list (decoded by ZoneView). NC_ITEM_BUY_REQ {itemid, lot} then buys an
    // item the open shop sells; the server deducts money (cheat it with GM &getmoney).
    // NPCMENUOPEN_ACK (Act cmd 29 = 0x201D): answers the menu a merchant/script NPC opens
    // after a click, selecting an option (1 = shop, from PurchaseSell.pcapng) — the server
    // then sends the SHOPOPEN sell list.
    private const ushort OpActNpcMenuAck = (ushort)(((int)ProtocolCommand.Act << 10) | 29); // 0x201D

    /// <summary>Open a merchant's shop and wait for its sell list. Mirrors the real client
    /// (PurchaseSell.pcapng): NPC-click → the server opens the NPC menu (NPCMENUOPEN_REQ) →
    /// reply NPCMENUOPEN_ACK with <paramref name="menuOption"/> (1 = shop) → the server sends
    /// the SHOPOPEN list (decoded into <see cref="ZoneView.ShopItems"/>). A plain click alone
    /// does NOT open the shop — the menu-ack is required.</summary>
    public async Task<ActionResult> OpenShopAsync(string id, ushort npcHandle, byte menuOption = 1, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        var view = handle.ZoneView;
        var hb = new byte[] { (byte)npcHandle, (byte)(npcHandle >> 8) };
        // CLOSE any previously-open shop FIRST. ROOT CAUSE of the "shop won't open" bug: you must
        // close shop A before opening shop B (a different nearby NPC) — the server won't open a new
        // shop while one is already open. The real client sends NC_ACT_ENDOFTRADE_CMD (0x200B, empty)
        // to clear that interaction state (it fires it 3× right after login to clear stale state too).
        // Without this, the 2nd shop's NPCCLICK gets the menu (0x201C) but NEVER the shop-open
        // (0x3C05/0x3C0x), and the sell is rejected 0x0383. Operator-confirmed; reproducible at Forest
        // of Mist (two adjacent travelling merchants). A relog also clears it, which is why a fresh
        // login's FIRST open always worked (and masked the bug as "intermittent").
        await s.SendAsync(new FiestaPacket(OpActEndOfTrade, ReadOnlyMemory<byte>.Empty), ct);
        // Discard any STALE menu flag BEFORE clicking, so we wait for the menu THIS click opens —
        // not a leftover NPCMENUOPEN_REQ from a previous attempt. Try up to 2 full click→menu→ack
        // cycles, confirming the SHOP actually opened (a shop-open packet, item 0x3C0x or soul-stone
        // 0x3C05 — ShopOpen) — NOT just the menu prompt. Selling into a menu that never resolved to a
        // shop is rejected 0x0383.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            view?.ClearNpcMenu();
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            // Wait for the server to open the NPC menu (triggered by our click), then select the option.
            for (var waited = 0; waited < 3000 && view?.NpcMenuOpen != true; waited += 100)
                await Task.Delay(100, ct);
            await s.SendAsync(new PROTO_NC_ACT_NPCMENUOPEN_ACK { ack = menuOption }, ct);
            view?.ClearNpcMenu();
            // Now wait for the actual shop-open packet (this is what makes SELL/BUY valid).
            for (var waited = 0; waited < 2500 && view?.ShopOpen != true; waited += 100)
                await Task.Delay(100, ct);
            if (view?.ShopOpen == true)
            {
                handle.Log($"open shop npc h={npcHandle} OPEN (menu-ack {menuOption}, attempt {attempt + 1})");
                return ActionResult.Sent;
            }
            handle.Log($"open shop npc h={npcHandle} — no shop-open after ack (attempt {attempt + 1}), retrying");
        }
        handle.Log($"open shop npc h={npcHandle} FAILED — no shop-open packet (sells would be rejected)");
        return ActionResult.Sent;
    }

    /// <summary>Sell <paramref name="lot"/> of the bag item at <paramref name="slot"/> to
    /// the open shop (NC_ITEM_SELL_REQ {slot, lot}). Verified in PurchaseSell.pcapng.</summary>
    public Task<ActionResult> SellAsync(string id, byte slot, uint lot, CancellationToken ct = default)
        => ActAsync(id, $"sell slot {slot} x{lot}",
            s => s.SendAsync(new PROTO_NC_ITEM_SELL_REQ { slot = slot, lot = lot }, ct));

    /// <summary>Enchant (upgrade) the gear in equip slot <paramref name="equip"/> using the
    /// enhancement stones at the given inventory slots (NC_ITEM_UPGRADE_REQ, GearEnchantment.pcapng).
    /// <paramref name="raw"/> = the primary stone (Elrue/Lixir/Xir by + range); the optional
    /// <paramref name="rawLeft"/>/<paramref name="rawMiddle"/>/<paramref name="rawRight"/> are
    /// the safety/bonus stones (red = prevent destroy, blue = prevent -1, gold = better chance);
    /// 0xFF = none. Outcomes: success (+N), no-change, downgrade, or destroy — read the result
    /// off the inventory / item-update broadcasts.</summary>
    public Task<ActionResult> EnchantAsync(string id, byte equip, byte raw,
        byte rawLeft = 0xFF, byte rawMiddle = 0xFF, byte rawRight = 0xFF, uint money = 0, CancellationToken ct = default)
        => ActAsync(id, $"enchant equip {equip} (raw={raw} l={rawLeft} m={rawMiddle} r={rawRight})",
            s => s.SendAsync(new PROTO_NC_ITEM_UPGRADE_REQ
            {
                equip = equip, raw = raw, raw_left = rawLeft, raw_middle = rawMiddle, raw_right = rawRight, gift_money = money
            }, ct));

    /// <summary>Buy <paramref name="number"/> HP soul-stone charges (NC_SOULSTONE_HP_BUY_REQ
    /// 0x5001) into the reserve that <see cref="UseSoulStoneHpAsync"/> draws from. Bought at a
    /// healer/soul-stone vendor; needs money. SP analogue: <see cref="BuySpStoneAsync"/>.</summary>
    public Task<ActionResult> BuyHpStoneAsync(string id, ushort number, CancellationToken ct = default)
        => ActAsync(id, $"buy HP soul-stone x{number}",
            s => s.SendAsync(new PROTO_NC_SOULSTONE_HP_BUY_REQ { number = number }, ct));

    /// <summary>Buy <paramref name="number"/> SP soul-stone charges (NC_SOULSTONE_SP_BUY_REQ 0x5002).</summary>
    public Task<ActionResult> BuySpStoneAsync(string id, ushort number, CancellationToken ct = default)
        => ActAsync(id, $"buy SP soul-stone x{number}",
            s => s.SendAsync(new PROTO_NC_SOULSTONE_SP_BUY_REQ { number = number }, ct));

    // NC_CHAR_REVIVE_REQ (Char cmd 78 = 0x104E): "move to respawn point" -> nearest town,
    // answered after death (DEADMENU 0x104D). The server then map-transitions to town.
    private const ushort OpCharReviveReq = 0x104E;

    /// <summary>Respawn after death — "move to respawn point" (NC_CHAR_REVIVE_REQ), which
    /// returns the char to the nearest town (a map transition the nav layer handles). Only
    /// meaningful while <see cref="ZoneView.Dead"/>. NOTE: a nearby cleric can revive in
    /// place (REVIVESAME) — a behaviour may prefer to wait for that before respawning to a
    /// possibly-far town; the server auto-respawns after ~2 min dead regardless.</summary>
    public Task<ActionResult> RespawnAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "respawn (move to respawn point -> nearest town)",
            s => s.SendAsync(new FiestaPacket(OpCharReviveReq, ReadOnlyMemory<byte>.Empty), ct));

    /// <summary>Buy <paramref name="lot"/> of item <paramref name="itemId"/> from the
    /// currently-open shop (NC_ITEM_BUY_REQ). The shop must be open (call
    /// <see cref="OpenShopAsync"/> first) and must sell the item; needs enough money.</summary>
    public Task<ActionResult> BuyAsync(string id, ushort itemId, uint lot, CancellationToken ct = default)
        => ActAsync(id, $"buy item {itemId} x{lot}",
            s => s.SendAsync(new PROTO_NC_ITEM_BUY_REQ { itemid = itemId, lot = lot }, ct));

    // ── Quests ────────────────────────────────────────────────────────────────
    /// <summary>Click an NPC (NC_ACT_NPCCLICK_CMD) — starts its quest dialogue / opens its
    /// menu. The server then drives the dialogue via NC_QUEST_SCRIPT_CMD_REQ.</summary>
    public Task<ActionResult> ClickNpcAsync(string id, ushort npcHandle, CancellationToken ct = default)
        => ActAsync(id, $"click npc h={npcHandle}",
            s => s.SendAsync(new FiestaPacket(OpActNpcClick, new[] { (byte)npcHandle, (byte)(npcHandle >> 8) }), ct));

    /// <summary>Answer a quest-dialogue step (NC_QUEST_SCRIPT_CMD_ACK). <paramref name="result"/>=1
    /// proceeds/accepts (the common case); branching quests read the answer from QuestData.shn.</summary>
    public Task<ActionResult> AnswerQuestAsync(string id, ushort questId, byte qsc, uint result = 1, CancellationToken ct = default)
        => ActAsync(id, $"quest {questId} answer qsc=0x{qsc:X2} result={result}",
            s => s.SendAsync(new PROTO_NC_QUEST_SCRIPT_CMD_ACK { nQuestID = questId, nQSC = qsc, nResult = result }, ct));

    /// <summary>Abandon a quest (NC_QUEST_GIVE_UP_REQ {questId}). Used to clear a
    /// persistence-glitched quest (loaded active but stuck at 0 progress) so it can be
    /// re-accepted fresh — the only way out of the status-8 stuck state.</summary>
    public Task<ActionResult> GiveUpQuestAsync(string id, ushort questId, CancellationToken ct = default)
        => ActAsync(id, $"quest {questId} give up",
            s => s.SendAsync(new PROTO_NC_QUEST_GIVE_UP_REQ { nQuestID = questId }, ct));

    /// <summary>Start a quest by id (NC_QUEST_START_REQ {questId}). The accept command for
    /// menu/remote quests where clicking the NPC opens a selection menu (0x201C) rather than a
    /// direct dialogue — the dialogue-only click can't accept those. Verified in Full.pcapng
    /// (0x4414 StartReq is "the accept" after browsing a giver's list). Drive the resulting
    /// accept dialogue with <see cref="DriveQuestDialogueAsync"/> afterward.</summary>
    public Task<ActionResult> StartQuestAsync(string id, ushort questId, CancellationToken ct = default)
    {
        // Stash the questId so the questId-less NC_QUEST_START_ACK can be attributed to it.
        if (_bots.TryGetValue(id, out var h)) h.ZoneView?.NoteQuestStartAttempt(questId);
        return ActAsync(id, $"quest {questId} start req",
            s => s.SendAsync(new PROTO_NC_QUEST_START_REQ { nQuestID = questId }, ct));
    }

    /// <summary>Answer the currently-pending quest dialogue step (from
    /// <see cref="ZoneView.PendingQuest"/>) — "proceed". Convenience so the caller needn't
    /// pass the quest id / qsc each step.</summary>
    public Task<ActionResult> ProceedQuestAsync(string id, uint result = 1, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var h) || h.ZoneView?.PendingQuest is not { } q)
            return Task.FromResult(ActionResult.NotFound);
        return AnswerQuestAsync(id, q.QuestId, q.Qsc, result, ct);
    }

    /// <summary>Drive a whole quest dialogue: click the NPC, then ACK each server-pushed
    /// script page (NC_QUEST_SCRIPT_CMD_REQ) with <paramref name="result"/> until the server
    /// stops sending pages. The quest script runs server-side — every SAY line is its own
    /// 0x4401 page that must be answered — so this loop walks the Start (accept), in-progress,
    /// or Finish (turn-in) script to completion in one call. <paramref name="result"/>=1 is the
    /// "proceed/accept" answer (the common case); at the script's IF-RESULT branch this is what
    /// accepts the quest. Stops when no new page arrives within the per-step timeout or after
    /// <paramref name="maxSteps"/>. Choice-reward turn-ins: the server sends a reward-select
    /// prompt (0x4412) on the [SHOW_REWARD] page; if <paramref name="rewardIndex"/> &gt;= 0 this
    /// answers it mid-dialogue with NC_QUEST_REWARD_SELECT_ITEM_INDEX (the RAW reward slot) BEFORE
    /// acking the "Complete" page — the order the real client uses (verified in Quest.pcapng).</summary>
    public async Task<ActionResult> DriveQuestDialogueAsync(string id, ushort npcHandle, uint result = 1, int rewardIndex = -1, int maxSteps = 24, ushort questId = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var h)) return ActionResult.NotFound;
        if (h.Phase != BotPhase.InZone || h.ZoneSession is not { } s) return ActionResult.NotInZone;
        var zv = h.ZoneView;

        // Baseline on the currently-pending step so we only answer pages that arrive AFTER
        // this click (a stale step from a previous dialogue must not be re-answered).
        var lastSeen = zv?.PendingQuest?.AtUtc ?? DateTime.MinValue;
        zv?.ClearRewardSelect();
        zv?.ClearNpcMenu();
        await s.SendAsync(new FiestaPacket(OpActNpcClick, new[] { (byte)npcHandle, (byte)(npcHandle >> 8) }), ct);
        // The real client ALWAYS follows NPCCLICK with STOP_REQ (0x2012) reporting the position it
        // halted at to talk — and the server only starts pushing the quest script (0x4401) AFTER that
        // STOP arrives (verified across every accept in QuestsLowLevel.pcapng: click→stop→0x4401→0x4402,
        // no menu for a plain quest giver). The bot used to click without STOP, so the server treated
        // the click as a bare/menu interaction and never drove the script. Send STOP at our current pos.
        if (h.Position is { } pos)
        {
            var stop = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), pos.X);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), pos.Y);
            await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
        }
        h.Log($"quest dialogue: click npc h={npcHandle} + stop, driving (result={result}, rewardIndex={rewardIndex})");

        // A quest GIVER that offers SEVERAL quests opens an NPC MENU (0x201C) on click — the quest
        // script (0x4401) only arrives AFTER we tell the server WHICH quest to advance. That is the
        // packet the operator flagged: NC_QUEST_SELECT_START_REQ (0x440F) {nNPCID, nQuestID}, keyed by
        // the NPC mobId (the menu's payload), not the entity handle. A single-quest giver sends the
        // script straight away (no menu, questId unused). So wait briefly: if a menu opens before any
        // quest page AND we were told which quest to take, select it. Without this the bot sits on the
        // unanswered menu → "0 pages answered" → nothing accepted.
        for (var w = 0; w < 1500
             && zv?.NpcMenuOpen != true
             && (zv?.PendingQuest?.AtUtc ?? DateTime.MinValue) <= lastSeen; w += 80)
            await Task.Delay(80, ct);
        if (zv?.NpcMenuOpen == true)
        {
            if (questId != 0)
            {
                var npcId = zv.MenuNpcId != 0 ? zv.MenuNpcId : (ushort)(zv.NearbyNpcs.FirstOrDefault(n => n.Handle == npcHandle)?.MobId ?? 0);
                await s.SendAsync(new PROTO_NC_QUEST_SELECT_START_REQ { nNPCID = npcId, nQuestID = questId }, ct);
                zv.ClearNpcMenu();
                h.Log($"quest dialogue: SELECT_START npc={npcId} quest={questId} (multi-quest menu)");
            }
            else
            {
                // No specific quest given → answer the menu with option 1 (NPCMENUOPEN_ACK) to reach the
                // quest dialogue. This is the single-quest-giver path (e.g. Remi opens a menu, option 1 =
                // its one quest). Removing this regressed the leveler into skipping every menu NPC.
                await s.SendAsync(new PROTO_NC_ACT_NPCMENUOPEN_ACK { ack = 1 }, ct);
                zv.ClearNpcMenu();
                h.Log($"quest dialogue: answered NPC menu (option 1) npc={zv.MenuNpcId} to reach the quest dialogue");
            }
        }

        bool rewardSelected = false;
        int answered = 0;
        for (int step = 0; step < maxSteps; step++)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2.5);
            QuestStep? cur = null;
            while (DateTime.UtcNow < deadline)
            {
                var pq = zv?.PendingQuest;
                if (pq is not null && pq.AtUtc > lastSeen) { cur = pq; break; }
                await Task.Delay(80, ct);
            }
            if (cur is null) break; // server sent no further page → dialogue finished
            lastSeen = cur.AtUtc;
            // On the [SHOW_REWARD] page (the one with the [Complete the Quest] button), the reward
            // MUST be chosen BEFORE acking it — the real client does ack→SELECT→ack (Quest.pcapng).
            // Acking it first = clicking Complete with no reward, which the server rejects/loops.
            // Detect the page by its dialog text (QuestDialog.shn carries the [SHOW_REWARD] tag).
            if (rewardIndex >= 0 && !rewardSelected &&
                (ClientData?.QuestDialog(cur.DialogId)?.Contains("SHOW_REWARD", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                await SelectQuestRewardAsync(id, cur.QuestId, (uint)rewardIndex, ct);
                rewardSelected = true;
                h.Log($"quest dialogue: reward-select quest={cur.QuestId} index={rewardIndex} (SHOW_REWARD dlg {cur.DialogId})");
            }
            await AnswerQuestAsync(id, cur.QuestId, cur.Qsc, result, ct);
            answered++;
        }
        h.Log($"quest dialogue done (npc h={npcHandle}, {answered} pages answered, rewardSelected={rewardSelected})");
        return ActionResult.Sent;
    }

    /// <summary>Select a quest reward item by index (NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD) —
    /// e.g. the class-appropriate reward.</summary>
    public Task<ActionResult> SelectQuestRewardAsync(string id, ushort questId, uint itemIndex, CancellationToken ct = default)
        => ActAsync(id, $"quest {questId} reward index {itemIndex}",
            s => s.SendAsync(new PROTO_NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD { nQuestID = questId, nSelectedItemIndex = itemIndex }, ct));

    /// <summary>Use an inventory item by slot (invenType: 0 = normal bag).</summary>
    public Task<ActionResult> UseItemAsync(string id, byte slot, byte invenType, CancellationToken ct = default)
        => ActAsync(id, $"use item slot={slot} type={invenType}",
            s => s.SendAsync(new PROTO_NC_ITEM_USE_REQ { invenslot = slot, invenType = invenType }, ct));

    /// <summary>Equip the inventory item at <paramref name="slot"/> (the server
    /// derives the target equipment slot from the item itself).</summary>
    public Task<ActionResult> EquipAsync(string id, byte slot, CancellationToken ct = default)
        => ActAsync(id, $"equip inventory slot {slot}",
            s => s.SendAsync(new PROTO_NC_ITEM_EQUIP_REQ { slot = slot }, ct));

    /// <summary>Pick up a ground item by its entity handle (NC_ITEM_PICK_REQ {itemhandle},
    /// the handle from <see cref="ZoneView.Drops"/> / a DROPEDITEM broadcast). Must be close
    /// to the item — use <see cref="LootAsync"/> to walk to it first. The server replies with
    /// CELLCHANGE (bag gains the item) + MAP_LOGOUT (item despawns) + PICK_ACK; success is
    /// judged by the bag change, not the ack error (see <see cref="ZoneView.PickResult"/>).
    /// Blocked when the inventory is full or the char is in a mini-house etc. — those failures
    /// aren't yet pinned to a code, so check the drop actually left view.</summary>
    public Task<ActionResult> PickupAsync(string id, ushort itemHandle, CancellationToken ct = default)
        => ActAsync(id, $"pickup item h={itemHandle}",
            s => s.SendAsync(new PROTO_NC_ITEM_PICK_REQ { itemhandle = itemHandle }, ct));

    /// <summary>Loot a ground drop: walk to it (pathfinding over the current map's grid),
    /// then pick it up and wait for it to leave view (picked) or a short cap. With
    /// <paramref name="itemHandle"/> 0 it loots the drop nearest the bot. Returns
    /// <see cref="ActionResult.NotFound"/> when nothing is on the ground. The convenience
    /// the combat loop calls after a kill to collect loot.</summary>
    public async Task<ActionResult> LootAsync(string id, ushort itemHandle = 0, double unitsPerSec = 120.0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        if (handle.ZoneView is not { } view || handle.Position is not { } pos) return ActionResult.NotInZone;

        var drop = itemHandle != 0
            ? view.Drops.FirstOrDefault(d => d.Handle == itemHandle)
            : view.NearestDrop(pos.X, pos.Y);
        if (drop is null) return ActionResult.NotFound;

        // Walk onto the item, then pick. Pickup has a short range, so stop right on it.
        await ApproachAsync(id, handle, drop.X, drop.Y, stopShort: 0, unitsPerSec, ct);
        await s.SendAsync(new PROTO_NC_ITEM_PICK_REQ { itemhandle = drop.Handle }, ct);
        handle.Log(BotLogLevel.Verbose, $"loot item {drop.ItemId} (h={drop.Handle}) @({drop.X},{drop.Y})");

        // Success = the drop left view (picked/despawned). PICK_ACK arrives too but its
        // error code was non-zero even on success, so the drop-gone signal is authoritative.
        var picked = await WaitUntilAsync(() => view.Drops.All(d => d.Handle != drop.Handle), 3000, ct);
        handle.Log(BotLogLevel.Info, picked ? $"looted h={drop.Handle}" : $"loot h={drop.Handle} unconfirmed (still on ground — inventory full / out of range / blocked?)");
        return ActionResult.Sent;
    }

    // Move/run (ACT MoverunCmd, 0x2019): 16 bytes = fromX,fromY,toX,toY (u32 LE).
    private static readonly ushort OpMoveRun =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.MoverunCmd);

    /// <summary>Max distance (world units) of a single MoverunCmd. The server rejects
    /// large jumps as teleport/anti-cheat — verified live (2026-06-11): a walk made of
    /// ~64u steps moved the character, but ~560–2536u straight segments (from
    /// path-simplification) did not move it server-side at all. So long segments are
    /// re-chunked into steps no bigger than this, mirroring how the real client streams
    /// movement. 250u matches the real client: MovementTypes.pcapng shows a single
    /// far mouse-click expand into a stream of ~250u MoverunCmd segments (the client
    /// pathfinds, the server does not), each accepted by the server.</summary>
    private const double MaxMoveStep = 250.0;

    /// <summary>Walk a precomputed path: stream MoverunCmd steps on a background task,
    /// paced to the bot's current <see cref="BotHandle.WalkSpeed"/> (updated live from
    /// MOVESPEED broadcasts) so the server accepts it as a normal walk at any speed.
    /// <paramref name="unitsPerSec"/> is a fallback when the handle has no WalkSpeed
    /// yet (shouldn't happen after zone entry). Long straight segments are sub-divided
    /// into <see cref="MaxMoveStep"/>-sized steps. Returns immediately; the walk
    /// continues until done or the bot is stopped.</summary>
    public ActionResult WalkPath(string id, IReadOnlyList<(uint X, uint Y)> waypoints, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session) return ActionResult.NotInZone;
        if (waypoints.Count < 2) return ActionResult.Sent;
        // Per-walk cancellation (linked to the bot's lifetime) so a MOVEFAIL can abort
        // just this walk. Replace/cancel any prior walk.
        var walkCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.WalkCts?.Cancel();
        handle.WalkCts = walkCts;
        var ct = walkCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var steps = 0;
                for (int i = 0; i < waypoints.Count - 1 && !ct.IsCancellationRequested; i++)
                {
                    var (fx, fy) = waypoints[i];
                    var (tx, ty) = waypoints[i + 1];
                    var segDist = Math.Sqrt(Math.Pow((double)tx - fx, 2) + Math.Pow((double)ty - fy, 2));
                    var subSteps = Math.Max(1, (int)Math.Ceiling(segDist / MaxStepFor(unitsPerSec)));
                    double cx = fx, cy = fy;
                    for (int k = 1; k <= subSteps && !ct.IsCancellationRequested; k++)
                    {
                        // Interpolate the next intermediate point along the segment.
                        var sx = (uint)Math.Round(fx + (tx - (double)fx) * k / subSteps);
                        var sy = (uint)Math.Round(fy + (ty - (double)fy) * k / subSteps);
                        var p = new byte[16];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), (uint)Math.Round(cx));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), (uint)Math.Round(cy));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), sx);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), sy);
                        await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                        handle.SetPosition(sx, sy); // advance tracked position as we walk
                        var stepDist = Math.Sqrt(Math.Pow(sx - cx, 2) + Math.Pow(sy - cy, 2));
                        cx = sx; cy = sy; steps++;
                        // Pace using the bot's live MOVESPEED-tracked walk speed.
                        // Falls back to the passed unitsPerSec if no broadcast yet.
                        var paceSpeed = handle.WalkSpeed > 0 ? handle.WalkSpeed : unitsPerSec;
                        await Task.Delay((int)Math.Clamp(stepDist / paceSpeed * 1000, 40, 2000), ct);
                    }
                }
                handle.Log(BotLogLevel.Verbose, $"walk-path done ({waypoints.Count} waypoints, {steps} move steps)");
            }
            catch (OperationCanceledException) { handle.Log("walk-path aborted (cancelled / move blocked)"); }
            catch (Exception ex) { handle.Log($"walk-path error: {ex.Message}"); }
            finally
            {
                if (ReferenceEquals(handle.WalkCts, walkCts)) handle.WalkCts = null;
                walkCts.Dispose();
            }
        }, ct);
        return ActionResult.Sent;
    }

    /// <summary>Walk from one map coordinate to another (one MoverunCmd step).</summary>
    public async Task<ActionResult> WalkAsync(string id, uint fromX, uint fromY, uint toX, uint toY, CancellationToken ct = default)
    {
        var result = await ActAsync(id, $"walk ({fromX},{fromY})->({toX},{toY})", s =>
        {
            var p = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), fromX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), fromY);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), toX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), toY);
            return s.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
        });
        if (result == ActionResult.Sent && _bots.TryGetValue(id, out var h)) h.SetPosition(toX, toY);
        return result;
    }

    /// <summary>Issue a GM command (e.g. <c>&amp;levelup 46</c>, <c>&amp;makeitem SafeProtection01</c>).
    /// GM commands are routed through the chat channel — the server processes the
    /// <c>&amp;</c>/<c>$</c> prefix when the account has GM authority (nAuthID=9).</summary>
    public Task<ActionResult> GmAsync(string id, string command, CancellationToken ct = default)
        => ActAsync(id, $"gm: {command}", s => s.SendAsync(ChatCodec.BuildChatReq(command), ct));

    /// <summary>Shared plumbing for a manual action on an in-zone bot: resolve →
    /// guard phase → send → log. This is the seam the HTTP endpoints (and later an
    /// LLM/Lua controller) drive.</summary>
    private async Task<ActionResult> ActAsync(string id, string logLine, Func<BotSession, Task> send)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session)
            return ActionResult.NotInZone;
        await send(session);
        handle.Log(logLine);
        return ActionResult.Sent;
    }

    /// <summary>Like <see cref="ActAsync"/> but sends on the bot's <b>WM</b> link
    /// (party / friend traffic is WorldManager-side). Requires the bot in zone with a
    /// live WM session.</summary>
    private async Task<ActionResult> WmActAsync(string id, string logLine, Func<BotSession, Task> send)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.WmSession is not { } wm) return ActionResult.NotInZone;
        await send(wm);
        handle.Log(logLine);
        return ActionResult.Sent;
    }

    /// <summary>WM action variant that also passes the bot's own character name (the
    /// <c>charid</c> Name5 the friend structs require).</summary>
    private Task<ActionResult> WmActAsync(string id, string logLine, Func<BotSession, string, Task> send)
        => WmActAsync(id, logLine, s =>
        {
            var self = _bots.TryGetValue(id, out var h) ? h.CharName ?? "" : "";
            return send(s, self);
        });

    /// <summary>Max single-MoverunCmd distance for a given walk speed. Anchored to the
    /// real client's ~250u segments at the ~120u/s on-foot speed; scales up with speed
    /// so a mount (higher move speed → larger client segments) gets proportionally
    /// bigger steps and keeps the per-second packet rate roughly constant.</summary>
    private static double MaxStepFor(double unitsPerSec) => Math.Max(MaxMoveStep, unitsPerSec * (MaxMoveStep / 120.0));

    /// <summary>React to a gate / town-portal transition: advance the tracked
    /// position to the new spawn coord and update the current map name. For an in-band
    /// change that's all the bot needs (it's still on the same connection). A
    /// cross-server handoff additionally needs a reconnect to the carried endpoint —
    /// that lives in the travel orchestrator; here we just record it and log.</summary>
    private void OnMapChanged(BotHandle handle, MapHandoff h, Action<string> log)
    {
        handle.SetPosition(h.X, h.Y);
        handle.BumpMapChange(); // wake any travel loop waiting on a transition
        // Resolve the destination map name. The handoff carries only the map id, so:
        // prefer what the catalog already knows; else, if a travel hop told us the
        // destination it's heading into (PendingDestMap), use that AND learn id↔name so
        // future routing/grids resolve it; else fall back to a synthetic id label. Doing
        // this here (not in the travel loop) makes the name deterministic before the
        // cross-server reconnect re-reads it — no race over the synthetic label.
        var name = Catalog.NameFor(h.MapId);
        if (name is null && handle.PendingDestMap is { } pending)
        {
            name = pending;
            Catalog.Learn(h.MapId, pending);
        }
        // Resolve the real short-name from the client MapInfo table (the wire only carries
        // the map id; the client looks the name up the same way). Fixes "map#<id>" labels
        // for transitions with no gate-LinkMap (town portals) — and lets navigation load
        // the right <name>.shbd grid.
        if (name is null && ClientData?.MapName(h.MapId) is { } clientName)
        {
            name = clientName;
            Catalog.Learn(h.MapId, clientName);
        }
        handle.SetCurrentMap(name ?? $"map#{h.MapId}");
        log($"[nav] now on {name} (mapId={h.MapId}) at ({h.X},{h.Y})" +
            (h.IsCrossServer ? $" — cross-server handoff to {h.Ip}:{h.Port}, reconnecting" : " (in-band)"));

        // In-band LINKSAME: re-send MAP_LOGINCOMPLETE so the server spawns us into the
        // new map and starts broadcasting its entities (mobs/NPCs/players). Without it
        // the bot sits in the destination invisible to the world (no mob packets). The
        // cross-server path doesn't need this — it does a full re-login (which sends it).
        if (!h.IsCrossServer && handle.ZoneSession is { } zs)
        {
            _ = zs.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
            log($"[nav] >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) to spawn into {name}");
        }
    }

    private async Task RunBotAsync(BotHandle handle)
    {
        var opt = handle.Options;
        var ct = handle.Cts.Token;
        void Log(string m) { handle.Log(m); _globalLog?.Invoke($"[{handle.Id}] {m}"); }

        FiestaClientConnectionScope wm = default;
        try
        {
            var chain = new LoginChain(_xorTable, Log);

            // If requested at spawn, start the packet dump NOW — before the first connect — so the
            // login handshake AND the zone-enter char-info burst (current soul-stone counts, vitals,
            // quest/skill seed) are captured, not just post-spawn traffic. The tap is threaded into
            // each connect; the existing /packetlog endpoint reuses the same handle.PacketLog.
            if (opt.PacketLog && handle.PacketLog is null)
            {
                var dir = Environment.GetEnvironmentVariable("PACKETLOG_DIR") ?? Directory.GetCurrentDirectory();
                handle.PacketLog = new Net.PacketLog(System.IO.Path.Combine(dir, $"packets-{handle.Id}.log"));
                Log($"packet log ENABLED (from spawn) -> {handle.PacketLog.Path}");
            }
            Action<bool, ushort, ReadOnlyMemory<byte>>? tap = handle.PacketLog is { } plog ? plog.Tap : null;

            handle.SetPhase(BotPhase.LoggingIn);
            var login = await chain.RunLoginAsync(
                new FiestaEndpoint(opt.Host, opt.LoginPort), opt.Credentials, opt.WorldNo, ct, tap);
            var wmPort = login.WmAdvertised.Port == 0 ? opt.WmPortFallback : login.WmAdvertised.Port;
            var wmEp = new FiestaEndpoint(opt.Host, wmPort);

            handle.SetPhase(BotPhase.SelectingChar);
            var (wmResult, wmConn) = await chain.RunWmAsync(
                wmEp, opt.Credentials, login.Otp, opt.Slot, opt.CreateSpec, ct, tap, opt.Character);
            wm = new FiestaClientConnectionScope(wmConn);

            if (wmResult.ZoneAdvertised is not { } zoneAdv || wmResult.Selected is not { } sel)
                throw new InvalidOperationException(
                    "account has no character to enter a zone (and no create spec)");
            handle.SetCharName(sel.Name);
            handle.SetLevel(sel.Level); // authoritative level from the WM avatar list (not inferred)
            handle.SetClass(sel.Class); // ClassID for class-appropriate quest-reward selection

            var zoneEntry = ZoneEntry.FromDataDir(_xorTable, Log, opt.DataDir);

            // The WM link stays open for the bot's whole in-zone life and across any
            // cross-server handoffs (each zone validates against a live WM session),
            // so its read loop runs once, in the background, for the duration.
            var wmSession = new BotSession(wmConn, sel.Name, wmResult.WmHandle, wmEp, Log,
                linkTag: "wm", logInbound: opt.LogInbound);
            handle.WmSession = wmSession;
            if (handle.PacketLog is { } plw) wmSession.PacketTap = plw.Tap; // re-attach packet log if enabled
            TrackPartyInvites(handle, wmSession); // capture incoming party invites (the inviter)
            var wmRun = wmSession.RunAsync(ct);

            // Zone (re-)entry loop. A normal stop/kick breaks out; a cross-server
            // handoff (NC_MAP_LINKOTHER) re-enters with the new endpoint + WM handle
            // the handoff carried. In-band changes (NC_MAP_LINKSAME) never reach here —
            // they're handled live on the same connection by OnMapChanged.
            var zoneEp = new FiestaEndpoint(opt.Host, zoneAdv.Port);
            var zoneWmHandle = wmResult.WmHandle;
            // The zone login ack has no map name, but the WM avatar list does
            // (PROTO_AVATARINFORMATION.loginmap) — that's the authoritative start map.
            // StartMap is only a fallback (e.g. for a just-created character).
            var currentMap = string.IsNullOrWhiteSpace(sel.LoginMap) ? opt.StartMap : sel.LoginMap;
            var firstEntry = true;
            while (true)
            {
                handle.SetPhase(BotPhase.EnteringZone);
                handle.ZoneSession = null; // no live zone link during (re)connect
                var entry = await zoneEntry.EnterAsync(zoneEp, zoneWmHandle, sel.Name, ct, tap);
                var zoneConn = entry.Conn;
                if (entry.SpawnX is { } spx && entry.SpawnY is { } spy) handle.SetPosition(spx, spy);
                if (entry.CharHandle is { } selfH) handle.SetSelfHandle(selfH);

                // Tripped to break THIS zone session when a cross-server handoff lands,
                // without disturbing the WM loop or the bot's overall cancellation.
                using var zoneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                MapHandoff? handoff = null;

                await using var zoneSession = new BotSession(zoneConn, sel.Name, zoneWmHandle, zoneEp, Log,
                    linkTag: "zone", logInbound: opt.LogInbound);
                handle.ZoneSession = zoneSession;
                if (handle.PacketLog is { } plz) zoneSession.PacketTap = plz.Tap; // re-attach packet log across handoff

                // Perception model (nearby players + chat) is always on — cheap, and the
                // status/say surface and any behavior read from it. The buff behavior is
                // opt-in via spawn options.
                using var zoneView = new ZoneView(zoneSession, Log, handle.Log);
                handle.ZoneView = zoneView;
                if (entry.CharHandle is { } selfH2) zoneView.SelfHandle = selfH2; // for MOVESPEED filtering
                zoneView.SelfPositionProvider = () => handle.Position; // for aggro (mob running at us)
                if (ClientData is { } cdata) zoneView.IsHuntableMob = mobId => cdata.IsHuntableEnemy(mobId); // ignore guards
                zoneView.SeedMaxVitals(entry.MaxHp, entry.MaxSp);
                zoneView.SeedMaxStones(entry.MaxHpStone, entry.MaxSpStone); // reserve capacity from [1802]
                zoneView.SeedStones(entry.CurHpStone, entry.CurSpStone); // real reserve from zone-enter char-info
                if (entry.Cen is { } cen0) zoneView.SeedMoney((long)cen0); // money from char-info — never leave it -1
                zoneView.SeedSkills(entry.Skills);
                zoneView.SeedPassives(entry.Passives);
                zoneView.SeedItems(entry.Items);
                zoneView.SeedQuests(entry.DoneQuests, entry.ActiveQuests, entry.ReadQuests);
                handle.SetCurrentMap(currentMap);
                zoneView.MapChanged += h =>
                {
                    handle.Emit(new BotEvent(BotEventKind.MapChanged, h));
                    OnMapChanged(handle, h, Log);
                    if (h.IsCrossServer) { handoff = h; zoneCts.Cancel(); } // break to reconnect
                };
                zoneView.MoveFailed += pos =>
                {
                    // Server rejected a move into an off-grid obstacle: resync to its
                    // truth and abort the current walk so we stop pushing into it.
                    handle.SetPosition(pos.X, pos.Y);
                    handle.WalkCts?.Cancel();
                    handle.Emit(new BotEvent(BotEventKind.MoveFailed, pos));
                    Log($"[nav] move blocked — resynced to ({pos.X},{pos.Y}), walk aborted");
                };
                zoneView.WalkSpeedChanged += speed => { handle.WalkSpeed = speed; };
                // Forward the perception events onto the stable per-bot hub so a looping
                // script keeps its subscriptions across a cross-server ZoneView swap.
                zoneView.ChatReceived += msg => handle.Emit(new BotEvent(BotEventKind.Chat, msg));
                zoneView.PlayerAppeared += p => handle.Emit(new BotEvent(BotEventKind.PlayerAppeared, p));
                zoneView.PlayerLeft += h => handle.Emit(new BotEvent(BotEventKind.PlayerLeft, h));
                zoneView.LevelChanged += lvl => { handle.SetLevel(lvl); handle.Log($"level up -> {lvl}"); };
                zoneView.HpChanged += hp => handle.Emit(new BotEvent(BotEventKind.Hp, hp));
                zoneView.SpChanged += sp => handle.Emit(new BotEvent(BotEventKind.Sp, sp));
                zoneView.Damaged += hit => handle.Emit(new BotEvent(BotEventKind.Hit, hit));
                var botId = handle.Id; // capture for the lambda
                zoneView.CastFailed += reason =>
                {
                    handle.Emit(new BotEvent(BotEventKind.CastFail, reason));
                    // Reactive cast-fail handling — lightweight, fire-and-forget.
                    // Runs on the session read loop so must not block; all actions
                    // go through SendAsync which serializes on the connection.
                    if (reason == ZoneView.CastFailReason.NotEnoughSp)
                    {
                        Log($"[combat] cast FAILED — not enough SP (0x{reason:X4}), recharging soul-stone");
                        _ = Task.Run(async () =>
                        {
                            try { await UseSoulStoneSpAsync(botId); }
                            catch (Exception ex) { Log($"[combat] soul-stone recharge error: {ex.Message}"); }
                        }, ct);
                    }
                    else if (reason == ZoneView.CastFailReason.OutOfRange)
                    {
                        Log($"[combat] cast FAILED — out of range (0x{reason:X4}), approaching target");
                        var tgt = handle.LastCastTarget;
                        var npcPos = tgt != 0 ? NpcPos(handle, tgt) : null;
                        if (npcPos is { } tp && handle.Position is { } pos)
                        {
                            // Use doubles for direction math to avoid uint underflow
                            // when tp.X < pos.X or tp.Y < pos.Y.
                            double dx = (double)tp.X - pos.X;
                            double dy = (double)tp.Y - pos.Y;
                            var dist = Math.Sqrt(dx * dx + dy * dy);
                            var step = Math.Min(dist - 1, MaxStepFor(120.0));
                            if (step > 0)
                            {
                                var nx = (uint)Math.Round(pos.X + dx / dist * step);
                                var ny = (uint)Math.Round(pos.Y + dy / dist * step);
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await WalkAsync(botId, pos.X, pos.Y, nx, ny, ct);
                                        var sk = handle.LastCastSkill;
                                        if (sk != 0)
                                        {
                                            if (handle.LastCastTarget == 0)
                                                await CastGroundAsync(botId, sk, tp.X, tp.Y, ct: ct);
                                            else
                                                await CastAsync(botId, sk, tgt, ct: ct);
                                        }
                                    }
                                    catch (Exception ex) { Log($"[combat] out-of-range retry error: {ex.Message}"); }
                                }, ct);
                            }
                        }
                    }
                };
                using var buff = opt.Buff is { } buffCfg
                    ? new BuffInTownBehavior(zoneSession, zoneView, buffCfg, Log, ct)
                    : null;

                handle.SetPhase(BotPhase.InZone);
                Log(firstEntry
                    ? $"*** {sel.Name} IN ZONE ({zoneEp}) — running until stopped ***"
                    : $"*** {sel.Name} RE-ENTERED ZONE ({zoneEp}, {currentMap}) after cross-server handoff ***");
                firstEntry = false;

                await zoneSession.RunAsync(zoneCts.Token);

                // A captured cross-server handoff (and not a real stop) means reconnect
                // to the carried endpoint with its WM handle and re-enter the zone.
                if (handoff is { IsCrossServer: true } ho && ho.Ip is { } ip && !ct.IsCancellationRequested)
                {
                    zoneEp = new FiestaEndpoint(ip, ho.Port);
                    zoneWmHandle = ho.WmHandle;
                    currentMap = handle.CurrentMap ?? currentMap;
                    Log($"[nav] reconnecting to zone {zoneEp} (wm={zoneWmHandle}) for cross-server handoff");
                    continue;
                }

                Log($"zone session ended — {zoneSession.State.DisconnectReason}");
                break;
            }

            // The zone loop ended for real (stop/kick). Wind down the WM link too.
            try { await wmRun; } catch (OperationCanceledException) { }
            handle.SetPhase(BotPhase.Stopped);
            Log($"sessions ended — wm: {wmSession.State.DisconnectReason}");
        }
        catch (OperationCanceledException)
        {
            handle.SetPhase(BotPhase.Stopped);
            Log("stopped (cancelled before zone entry)");
        }
        catch (Exception ex)
        {
            handle.SetError($"{ex.GetType().Name}: {ex.Message}");
            handle.SetPhase(BotPhase.Failed);
            Log($"[FAIL] {handle.Error}");
        }
        finally
        {
            wm.Dispose(); // zoneConn is owned/disposed by the zoneSession's DisposeAsync
        }
    }

    public async ValueTask DisposeAsync()
    {
        var handles = _bots.Values.ToArray();
        foreach (var h in handles) h.Cts.Cancel();
        foreach (var h in handles)
        {
            if (h.RunTask is { } task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* best-effort shutdown */ }
            }
            h.Cts.Dispose();
        }
        _bots.Clear();
    }

    /// <summary>Disposes the WM connection exactly once, even if it was never set
    /// (failure before the WM phase). The zone connection is owned by its session.</summary>
    private readonly struct FiestaClientConnectionScope(Net.FiestaClientConnection? conn) : IDisposable
    {
        public void Dispose() => conn?.Dispose();
    }
}
