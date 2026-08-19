using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Metrics;
using Fiesta.Bot.Navigation;
using Fiesta.Bot.Pathfinding;
using Fiesta.Bot.Scripting;
using Fiesta.Bot.Session;
using Fiesta.Bot.Zone;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Manager;

/// <summary>Owns N bots in parallel, keyed by id</summary>
public sealed class BotManager : IAsyncDisposable
{
    private readonly byte[] _xorTable;
    private readonly Action<string>? _globalLog;
    private readonly ConcurrentDictionary<string, BotHandle> _bots = new(StringComparer.OrdinalIgnoreCase);
    private int _seq;

    /// <summary>The world map graph, learned by play (gates seen in each map) and shared across all bots — so one bot's explor…</summary>
    public MapGraph Graph { get; } = new();

    /// <summary>Map id↔short-name resolver, learned from gate links and (optionally) seeded from a BYO MapInfo dump via MAPINF…</summary>
    public MapCatalog Catalog { get; } = new();

    /// <summary>Resolves a map short-name to its walkability grid (BYO, from BLOCKINFO_DIR )</summary>
    public Func<string, BlockGrid?>? GridProvider { get; set; }

    /// <summary>Per-map instance-door provider (from BLOCKINFO_DIR/&amp;lt;Map&amp;gt;.sbi )</summary>
    public Func<string, IReadOnlyList<Fiesta.Bot.Navigation.InstanceDoor>?>? DoorProvider { get; set; }
    /// <summary>Per-map scenario-area provider (from BLOCKINFO_DIR/&amp;lt;Map&amp;gt;.aid )</summary>
    public Func<string, IReadOnlyList<Fiesta.Bot.Navigation.ScenarioArea>?>? AreaProvider { get; set; }

    /// <summary>BYO client game-data reader (SHN tables from the operator's ressystem )</summary>
    public GameData.ClientData? ClientData { get; set; }

    /// <summary>Durable, per-server store of learnt NPC shop classifications (skip re-probing a town that's already been class…</summary>
    public NpcKnowledge Knowledge { get; } = new();

    public BotManager(byte[] xorTable, Action<string>? globalLog = null)
    {
        _xorTable = xorTable;
        _globalLog = globalLog;
        var seeded = Catalog.LoadSeedFromEnv();
        if (seeded > 0) _globalLog?.Invoke($"[nav] MapCatalog seeded {seeded} maps from MAPINFO_PATH");
    }

    /// <summary>Start a bot. Non-blocking — the login chain runs in the background; watch the returned handle for progress</summary>
    public BotHandle Spawn(BotSpawnOptions options)
    {
        var id = options.Id ?? $"b{Interlocked.Increment(ref _seq)}";
        var handle = new BotHandle(id, options);
        if (!_bots.TryAdd(id, handle))
        {
            var existing = _bots.TryGetValue(id, out var e) ? e : null;
            if (existing is null || existing.Phase is not (BotPhase.Stopped or BotPhase.Failed))
                throw new InvalidOperationException(
                    $"a bot with id '{id}' already exists and is {(existing?.Phase.ToString() ?? "live")} — " +
                    "stop it first if you mean to replace it");
            _globalLog?.Invoke($"[{id}] spawn over a {existing.Phase} bot — replacing the dead handle " +
                               "(it cannot recover on its own; this is what used to 409)");
            try { existing.Cts.Cancel(); } catch { }
            _bots.TryRemove(new KeyValuePair<string, BotHandle>(id, existing));
            if (!_bots.TryAdd(id, handle))
                throw new InvalidOperationException($"a bot with id '{id}' was re-created concurrently");
        }

        // Log/UI readability: rewrite every bare `mob ` token into "Marlone (Id 22)" (operator 2026-08-05 — "I don't wan…
        handle.MobNameResolver = mobId => ClientData?.Mob(mobId)?.Name;

        handle.Log($"spawn requested: {options.Host}:{options.LoginPort} user='{options.Credentials.Username}'");
        // Remember that this bot SHOULD BE RUNNING so a pod restart can restore it without a human
        Knowledge?.SaveRosterEntry(id, options);
        // Phase accounting must SURVIVE this respawn — a fresh BotHandle would otherwise start at zero and silently disc…
        if (Knowledge is { } kn)
        {
            handle.SeedPhaseSeconds(kn.LoadPhaseSeconds(id));
            handle.PhasePersist = kn.SavePhaseSeconds;
            // The tail, on disk: restore what earlier sessions wrote BEFORE the first new line is logged, so /log reads as o…
            var logDir = kn.LogDir;
            handle.SeedLogFromDisk(BotLogFile.LoadRecent(logDir, id, 20_000));
            handle.LogFile = new BotLogFile(logDir, id);
        }
        handle.RunTask = Task.Run(() => RunBotAsync(handle));
        return handle;
    }

    public IReadOnlyList<BotHandle> List() => _bots.Values.OrderBy(b => b.Id).ToArray();

    public BotHandle? Get(string id) => _bots.TryGetValue(id, out var h) ? h : null;

    /// <summary>Toggle a tailable, both-directions, plaintext (XOR-decoded) packet log for a bot — every S→C and C→S frame, in…</summary>
    /// <summary>Turn chat narration on/off AND remember it. Writing only the runtime flag is why it kept reverting:
    /// the handle is rebuilt on every respawn and pod rollout, so the setting has to go back into the roster record
    /// that the rebuild replays -- exactly how PacketLog survives.</summary>
    public bool? SetAnnounce(string id, bool enabled)
    {
        if (!_bots.TryGetValue(id, out var handle)) return null;
        handle.AnnounceChat = enabled;
        Knowledge?.SaveRosterEntry(id, handle.Options with { Announce = enabled });
        handle.LogOperatorAction($"[announce] chat narration {(enabled ? "ON" : "OFF")} (persisted for reconnects)");
        return enabled;
    }

    public (bool Found, bool Enabled, string? Path) SetPacketLog(string id, bool enabled)
    {
        if (!_bots.TryGetValue(id, out var handle)) return (false, false, null);

        if (enabled)
        {
            if (handle.PacketLog is { } already)
            {
                // Make sure the current sessions are tapped
                if (handle.ZoneSession is { } zs) zs.PacketTap = handle.CombinedTap;
                if (handle.WmSession is { } ws) ws.PacketTap = handle.CombinedTap;
                return (true, true, already.Path);
            }
            var dir = Environment.GetEnvironmentVariable("PACKETLOG_DIR") ?? Directory.GetCurrentDirectory();
            var path = System.IO.Path.Combine(dir, $"packets-{id}.log");
            var log = new Net.PacketLog(path);
            handle.PacketLog = log;
            if (handle.ZoneSession is { } zs2) zs2.PacketTap = handle.CombinedTap;
            if (handle.WmSession is { } ws2) ws2.PacketTap = handle.CombinedTap;
            handle.Log($"packet log ENABLED -> {path}");
            return (true, true, path);
        }
        else
        {
            // Do NOT null the tap here — that would switch off the always-on PacketRing too
            if (handle.ZoneSession is { } zs) zs.PacketTap = handle.CombinedTap;
            if (handle.WmSession is { } ws) ws.PacketTap = handle.CombinedTap;
            var path = handle.PacketLog?.Path;
            handle.PacketLog?.Dispose();
            handle.PacketLog = null;
            if (path is not null) handle.Log("packet log DISABLED");
            return (true, false, path);
        }
    }

    /// <summary>Signal a bot to stop and wait (briefly) for it to wind down</summary>
    public async Task<bool> StopAsync(string id, CancellationToken ct = default, bool forget = true)
    {
        if (!_bots.TryGetValue(id, out var handle)) return false;
        handle.Log("stop requested");
        // Tear down any looping behaviour script/graph first so it stops issuing actions
        handle.ScriptRunner?.Dispose();
        handle.ScriptRunner = null;
        foreach (var g in handle.GraphRunners.Values) g.Dispose();
        handle.GraphRunners.Clear();
        var inZone = handle.Phase == BotPhase.InZone && handle.ZoneSession is { } zs0 && handle.WmSession is not null;
        if (handle.Phase is not (BotPhase.Stopped or BotPhase.Failed))
            handle.SetPhase(BotPhase.Stopping);

        if (inZone)
        {
            // Clean logout: send the quit frames (zone: LOGOUTREADY+quit, WM: quit), then DON'T cancel — keep the sessions r…
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
            if (handle.WmSession is { } wmAlive && wmAlive.State.Connected)
            {
                using var wmLogoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await wmAlive.LogoutAsync(logoutReady: false, wmLogoutCts.Token);
                    handle.Log("stop: no zone link, but the WM link was still open — logged it out so no stale session is left behind");
                }
                catch (Exception ex) { handle.Log($"stop: WM logout failed ({ex.GetType().Name}) — a stale session may linger until the server times it out"); }
            }
            handle.Cts.Cancel();
        }

        if (handle.RunTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (TimeoutException) { handle.Log("stop: lifecycle task did not finish within 10s"); }
            catch (OperationCanceledException) { }
        }
        _bots.TryRemove(id, out _);
        // An EXPLICIT stop means do not bring this one back on the next restart
        if (forget) Knowledge?.ForgetRosterEntry(id);
        handle.Cts.Dispose();
        return true;
    }

    /// <summary>Clean logout + re-login IN PLACE (operator 2026-07-14): StopAsync (clean logout) → re-Spawn with the same opti…</summary>
    public bool Relog(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return false;
        var opts = handle.Options;
        var (sname, ssrc, stick) = (handle.LastScriptName, handle.LastScriptSource, handle.LastScriptTickMs);
        // CRITICAL, and tagged so it can never be missed again
        handle.NoteEvent("relog", $"prevSessionSeconds={(handle.ZoneEnteredUtc == DateTime.MinValue ? -1 : (DateTime.UtcNow - handle.ZoneEnteredUtc).TotalSeconds):F0}");
        handle.Log($"⛔ CRITICAL: RELOG — clean logout → re-login → re-apply script. " +
                   $"A relog costs a full town pass; if these repeat, THAT is the levelling blocker.");
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync(id, forget: false);
                // BOUNDED RETRY (tickets.md P1, 2026-07-29): a TRANSIENT re-login failure — observed live as `SocketException: R…
                const int maxAttempts = 4;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (attempt == 1) await Task.Delay(3000);                 // let the logout settle (avoid dup-login kick)
                    else { try { await StopAsync(id, forget: false); } catch { } await Task.Delay(5000); }  // clean up the Failed handle, back off (NEVER forget: see above)
                    Spawn(opts);
                    var reachedZone = false;
                    for (int i = 0; i < 90; i++)           // wait up to 90s for zone re-entry
                    {
                        await Task.Delay(1000);
                        if (!_bots.TryGetValue(id, out var nh)) break;        // handle gone (external stop) — abort
                        if (nh.Phase == BotPhase.InZone)
                        {
                            if (ssrc is not null) ApplyScript(id, sname ?? "level_quest", ssrc, stick <= 0 ? 400 : stick);
                            reachedZone = true; break;
                        }
                        if (nh.Phase == BotPhase.Failed) break;               // transient re-login failure → retry
                    }
                    if (reachedZone) return;
                    _globalLog?.Invoke($"[{id}] RELOG attempt {attempt}/{maxAttempts} didn't reach zone (transient re-login failure) — {(attempt < maxAttempts ? "retrying" : "giving up; next cron will respawn")}");
                }
            }
            catch (Exception ex) { _globalLog?.Invoke($"[{id}] RELOG error: {ex.Message}"); }
        });
        return true;
    }

    // Behaviour scripting (Lua) ───────────────────────────────────────────── Apply a Lua behaviour script to a bot…
    internal static string ScriptKeyForId(string id) => $"botid|{id}";

    public BotScriptRunner? ApplyScript(string id, string name, string source, int tickMs = 250, bool trace = false)
    {
        if (!_bots.TryGetValue(id, out var handle)) return null;
        handle.ScriptRunner?.Dispose();               // replace any running script
        void ScriptLog(string m) { handle.Log(m); _globalLog?.Invoke($"[{id}] {m}"); }
        var runner = new BotScriptRunner(handle, new BotApi(this, handle), name, source, ScriptLog, handle.Cts.Token, tickMs, trace);
        handle.ScriptRunner = runner;
        handle.LastScriptName = name; handle.LastScriptSource = source; handle.LastScriptTickMs = tickMs; // for bot.relog re-apply
        // PERSIST it too. The fields above are process memory, so a pod restart loses them and the watchdog below has no…
        Knowledge?.SaveScript(handle.KnowledgeScope, name, source, tickMs);
        Knowledge?.SaveScript(ScriptKeyForId(id), name, source, tickMs);
        handle.Log($"script '{name}' applied ({source.Length} chars, tick={tickMs}ms{(trace ? ", trace" : "")})");
        runner.Start();
        return runner;
    }

    /// <summary>Stop a bot's looping script (no-op if none)</summary>
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

    /// <summary>Where the bot's last Lua tick went (null if no bot / no script)</summary>
    public Scripting.BotScriptRunner.ProfileSnapshot? ScriptProfile(string id)
        => _bots.TryGetValue(id, out var handle) ? handle.ScriptRunner?.Profile : null;

    /// <summary>Debug status of a bot's running script (null if no bot / no script)</summary>
    public ScriptStatus? ScriptStatus(string id)
        => _bots.TryGetValue(id, out var handle) ? handle.ScriptRunner?.Status() : null;

    // Behaviour graph (state machine) ─────────────────────────────────────── Disk-persisted behaviour-graph library…
    public Scripting.GraphStore Graphs { get; set; } = new("behavior-graphs");

    /// <summary>Apply a behaviour graph to a bot and start it (replaces any running script/graph)</summary>
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

    /// <summary>Stop a bot's behaviour graph by , or ALL graphs if null</summary>
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

    /// <summary>Request a transition to in graph (or the only running graph if null)</summary>
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

    /// <summary>Status of all behaviour graphs running on a bot (empty if none)</summary>
    public IReadOnlyList<ScriptStatus> GraphStatus(string id)
        => _bots.TryGetValue(id, out var handle) ? handle.GraphRunners.Values.Select(g => g.Status()).ToArray() : [];

    /// <summary>Outcome of a manual in-zone action</summary>
    public enum ActionResult { Sent, NotFound, NotInZone }

    /// <summary>Declare + wire this bot's metrics to real packet events</summary>
    private static void RegisterMetrics(BotHandle handle, ZoneView zv)
    {
        var m = handle.Metrics;
        // Gauges: levels sampled over time
        m.InitMetric("hp", MetricDirection.HigherIsBetter);
        m.InitMetric("sp", MetricDirection.HigherIsBetter);
        m.InitMetric("hpPct", MetricDirection.HigherIsBetter);
        m.InitMetric("aggressors", MetricDirection.LowerIsBetter);
        m.InitMetric("hpStones", MetricDirection.HigherIsBetter);
        // Counters: amounts that must ADD UP within a batch, not average
        m.InitMetric("damageDealt", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("damageTaken", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("expGained", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("expLostToDeath", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("deaths", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("kills", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("skillHits", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("healsLanded", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("healsFailed", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("moneyDelta", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("distance", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("mapChanges", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("stuns", MetricDirection.LowerIsBetter, MetricKind.Counter);
        // Split from stuns 2026-08-06: both block movement, but a root still lets us cast
        m.InitMetric("roots", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("doorsOpened", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("moveFails", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("itemsPickedUp", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("pickupFails", MetricDirection.LowerIsBetter, MetricKind.Counter);
        m.InitMetric("questMobKills", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("mounts", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("dismounts", MetricDirection.HigherIsBetter, MetricKind.Counter);
        m.InitMetric("secondsMounted", MetricDirection.HigherIsBetter, MetricKind.Counter);

        // ── wire to the wire ────────────────────────────────────────────────────────────────────────
        zv.MetricSink = (name, val) => m.LogMetric(name, val);
        zv.BotEventSink = (kind, detail) => handle.NoteEvent(kind, detail);
        zv.HpChanged += hp =>
        {
            m.LogMetric("hp", hp);
            if (zv.MaxHp > 0) m.LogMetric("hpPct", 100.0 * hp / zv.MaxHp);
        };
        zv.SpChanged += sp => m.LogMetric("sp", sp);
        zv.MoveFailed += _ => m.LogMetric("moveFails", 1);
        zv.MapChanged += _ => m.LogMetric("mapChanges", 1);
        // Sampled-on-change rather than polled: these only move when something happens, and a gauge with no new samples…
        zv.HpChanged += _ =>
        {
            m.LogMetric("aggressors", zv.Aggressors.Count);
            if (zv.HpStones is { } st) m.LogMetric("hpStones", st);
        };
    }

    /// <summary>Make a bot say in its zone (local chat)</summary>
    public Task<ActionResult> SayAsync(string id, string text, CancellationToken ct = default)
        => ActAsync(id, $"say: \"{text}\"", s => s.SendAsync(ChatCodec.BuildChatReq(text), ct));

    /// <summary>Whisper to the player named</summary>
    public Task<ActionResult> WhisperAsync(string id, string to, string text, CancellationToken ct = default)
        => ActAsync(id, $"whisper {to}: \"{text}\"", s => s.SendAsync(ChatCodec.BuildWhisperReq(to, text), ct));

    // The real client's cast sequence (from Z:/Buff.pcapng): TARGET the handle (BAT TargettingReq), switch to battle…
    private static readonly ushort OpBatTarget =
        (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.TargettingReq);
    private static readonly ushort OpActChangeMode =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.ChangemodeReq);

    /// <summary>Say something in game chat IF announcing is switched on for this bot. Everything that narrates goes
    /// through here so there is exactly one rate limit: the server will throttle or drop a chat flood, and a dropped
    /// line is worse than no line because it reads as "that never happened". Identical consecutive text is also
    /// suppressed -- a tactic that re-asserts every tick would otherwise bury the change you are watching for.</summary>
    public void Announce(BotHandle handle, string text)
    {
        if (!handle.AnnounceChat || string.IsNullOrWhiteSpace(text)) return;
        var now = DateTime.UtcNow;
        if ((now - handle.LastAnnounceUtc).TotalMilliseconds < AnnounceMinGapMs) return;
        if (string.Equals(text, handle.LastAnnounceText, StringComparison.Ordinal)) return;
        handle.LastAnnounceUtc = now; handle.LastAnnounceText = text;
        _ = SayAsync(handle.Id, text.Length > 90 ? text[..90] : text);
    }

    /// <summary>Minimum gap between chat announcements. Chat is rate-limited server-side and shared with real players.</summary>
    private const int AnnounceMinGapMs = 1200;

    /// <summary>Cast a skill on a target zone handle, replaying the client's target → battle-mode → (face/stop) → cast sequenc…</summary>
    public async Task<ActionResult> CastAsync(string id, ushort skill, ushort target, bool? stopFirst = null, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        // Record the cast attempt so the cast-fail reactive layer (CastFailed subscriber) can retry the same skill+targe…
        handle.LastCastSkill = skill;
        handle.LastCastTarget = target;
        var (needFace, needStop) = ResolveFaceStop(skill, stopFirst);
        // SKILL-CAST THROUGHPUT in an instance (P0 tick 78, boss-fight DPS): FaceAndStop's tiny MOVERUN step MOVEFAILs i…
        if (handle.ZoneView is { } zv && zv.InScenarioInstance
            && zv.LastRealDamageDealtAtUtc > DateTime.MinValue
            && (DateTime.UtcNow - zv.LastRealDamageDealtAtUtc).TotalMilliseconds < 2500)
        {
            needFace = false; needStop = false;
        }
        // IT IS THE **MOVERUN**, NOT THE STOP, THAT KILLS THE AUTO-ATTACK (Z:/CombatPriest.pcapng, operator's deliberate…
        var adjust = NeedsFacingAdjust(handle, skill, target);
        if (!adjust) { needFace = false; needStop = false; }

        if (needFace && handle.ZoneView is { BashActive: true } && handle.BashTarget == target)
        {
            needFace = false; needStop = true;
            handle.Log(BotLogLevel.Verbose, $"cast {skill}: face-step suppressed — bash is running on this target");
        }

        // FIX 0: NEVER SEND A CAST THE CLIENT KNOWS IS NOT READY ──────────────────────────────── The real client sent Z…
        if (ClientData?.Skill(skill) is { } si0 && handle.ZoneView is { } zvcd)
        {
            var readyIn = zvcd.SkillReadyInMs(skill, si0.DelayTimeMs, si0.CastTimeMs);
            if (readyIn > 0)
            {
                handle.Log(BotLogLevel.Verbose, $"cast {skill} NOT SENT — {readyIn:F0}ms of cooldown left");
                return ActionResult.Sent;   // the real client would not have transmitted either
            }
        }

        // CAST RANGE: NEVER ASK FOR A CAST THE TARGET IS TOO FAR AWAY FOR ────────────────────── 0x0FCA decodes (from the client's own switch — docs/ERROR_CODE_RUNBOOK.md) to "The target is out of casting range…
        if (ClientData?.Skill(skill) is { } sr && handle.Position is { } mp
            && NpcPos(handle, target) is { } tpos)
        {
            var reach = sr.Range > 0 ? sr.Range : (handle.ZoneView?.LearnedMeleeRange ?? 0);
            var dx = (double)tpos.X - mp.X; var dy = (double)tpos.Y - mp.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (reach > 0 && dist > reach)
            {
                handle.Log(BotLogLevel.Verbose,
                    $"cast {skill} NOT SENT — target {dist:F0}u away, skill reaches {reach:F0}u " +
                    $"({(sr.Range > 0 ? "ActiveSkill.Range" : "learned melee reach; skill declares Range=0")}) — 0x0FCA is RANGE");
                return ActionResult.Sent;   // the caller closes the distance; we do not burn a STOP
            }
        }

        if (handle.ZoneView is { } zvw && zvw.BashActive
            && handle.LastBashSentUtc > DateTime.MinValue
            && (DateTime.UtcNow - handle.LastBashSentUtc).TotalMilliseconds < BashWindupMs
            && zvw.LastRealDamageDealtAtUtc < handle.LastBashSentUtc)
        {
            handle.Log(BotLogLevel.Verbose,
                $"cast {skill} deferred — bash {(DateTime.UtcNow - handle.LastBashSentUtc).TotalMilliseconds:F0}ms ago has not swung yet");
            return ActionResult.Sent;   // not an error: we simply let the swing land first
        }

        // FIX 2 of 3: ONLY RE-TARGET WHEN THE TARGET ACTUALLY CHANGES ─────────────────────────── We sent NC_BAT_TARGETT…
        if (handle.CurrentTarget != target || !handle.TargetAsserted)
        {
            await s.SendAsync(new FiestaPacket(OpBatTarget, new byte[] { (byte)target, (byte)(target >> 8) }), ct);
            handle.CurrentTarget = target; handle.TargetAsserted = true; handle.TargetSetAtUtc = DateTime.UtcNow;
            if (handle.ZoneView is { } zvT) zvT.CurrentTargetHandle = target;   // so a death can invalidate it
            // ⚠️ A CAST DOES CARRY ITS TARGET. PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ is {skill u16 @0,
            // target u16 @2} (PDB), and the send below puts this handle straight into it -- the server does
            // NOT fall back to its current selection to decide what we hit. The comment that used to sit here
            // said the opposite, and it is load-bearing misinformation: it makes every 0x0FCA look like a
            // target-desync, which is a theory that has been chased repeatedly and is disproved on our own
            // wire (23 casts, cast-target == last-targetted handle every time, 5 of them still failed).
            // We still send TARGETTING first because the real client does (Z:/CombatExtensive.pcapng).
            var ok = await AwaitTargetConfirmAsync(handle, target, ct);
            handle.Log(BotLogLevel.Verbose,
                $"cast {skill}: target h={target} {(ok ? "CONFIRMED" : "NOT confirmed (timeout)")} after " +
                $"{(DateTime.UtcNow - handle.TargetSetAtUtc).TotalMilliseconds:F0}ms");
        }
        await EnsureBattleModeAsync(handle, s, ct);
        // Record WHICH of the three pre-cast paths ran
        string sentPath;
        if ((needFace || needStop) && NpcPos(handle, target) is { } tp)
        { await FaceAndStopAsync(handle, s, tp.X, tp.Y, ct); sentPath = "face+stop+cast"; }
        else if (!adjust)
        { await StopOnlyAsync(handle, s, ct); sentPath = "stop+cast"; }   // STOP without the swing-breaking MOVERUN
        else
        { sentPath = "cast-only(NO-STOP)"; }   // needFace/needStop false AND adjust true — nothing committed
        handle.ZoneView?.NoteCastAttempt(skill, target);   // capture geometry BEFORE the wire, for [castfail]
        await s.SendAsync(new PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ { skill = skill, target = target }, ct);
        if (handle.ZoneView is { } zvc) zvc.NoteCastSent(ClientData?.Skill(skill)?.CastTimeMs ?? 0);
        if (handle.ZoneView is { } zvs) zvs.NoteSkillCast(skill);   // per-skill, for the cooldown panel
        var g = _lastCastGeom;
        handle.Log(BotLogLevel.Info,
            $"[castgeom] skill={skill} h={target} dist={g.Dist:F0} reach={g.Range:F0} " +
            $"offBy={(g.OffByDeg < 0 ? "n/a" : $"{g.OffByDeg:F0}°")} arc={g.ArcDeg}° " +
            $"({g.Note}) sent={sentPath}");
        handle.Log(BotLogLevel.Verbose, $"cast skill {skill} on h={target} (target+mode+{sentPath})");
        return ActionResult.Sent;
    }

    /// <summary>Decide whether a cast must face the target and/or STOP first</summary>
    private (bool NeedFace, bool NeedStop) ResolveFaceStop(ushort skill, bool? stopFirst)
    {
        if (stopFirst is { } force) return (force, force);
        if (ClientData?.Skill(skill) is { } si) return (si.UsableDegree > 0, !si.IsMovingSkill);
        return (true, true); // no data → proven default
    }

    /// <summary>Cast a location-targeted (ground / AoE) skill at a world coordinate</summary>
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

    /// <summary>Turn to face ( , ) and STOP there</summary>
    private static async Task EnsureBattleModeAsync(BotHandle handle, BotSession s, CancellationToken ct)
    {
        // THERE IS NO SELF-ACK FOR BATTLE MODE. 0x2009 is NC_ACT_SOMEONEchangemode -- it reports OTHER entities,
        // which is why SelfInBattleMode stayed null forever and this method never trusted its own send: it
        // re-sent every 500ms and let every cast in between go out in NON-battle mode, failing 0x0FC0. Measured
        // 2026-08-18 on FighterFresh: 501 failures/minute, 197 sends in 2 minutes each logging "nothing yet",
        // and ZERO damage dealt.
        // Ground truth, Z:/CombatExtensive.pcapng (real client, port 9016):
        //     S<- 0x243D NC_BAT_CEASE_FIRE_CMD      <- the server ENDS combat; we are out of battle mode
        //     C-> 0x2008 NC_ACT_CHANGEMODE_REQ      <- client re-enters, waits for NOTHING
        //     C-> 0x2012 NC_ACT_STOP_REQ
        //     C-> 0x242B NC_BAT_BASHSTART_CMD       -> SWING_START / SWING_DAMAGE follow
        // So: we are in battle mode from the moment we send the REQ, and we leave it when CEASE_FIRE lands on
        // our handle. Re-send only when the last CEASE_FIRE is NEWER than our last request.
        var ceased = handle.ZoneView?.LastBashCeasedAtUtc ?? DateTime.MinValue;
        if (handle.InBattleMode && ceased <= handle.LastBattleModeSentUtc) return;
        if (handle.ZoneView?.SelfInBattleMode == true) { handle.InBattleMode = true; return; }

        // Spam guard only -- NOT the correctness gate. Without the check above this was the bug: it skipped the
        // send and then cast anyway.
        if ((DateTime.UtcNow - handle.LastBattleModeSentUtc).TotalMilliseconds < 200) return;
        handle.LastBattleModeSentUtc = DateTime.UtcNow;
        await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);
        handle.InBattleMode = true;
        handle.Log(BotLogLevel.Verbose,
            $"battle mode asserted (last CEASE_FIRE {(ceased == DateTime.MinValue ? "none" : ceased.ToString("HH:mm:ss.fff"))})");
    }

    /// <summary>Mount item class in ItemInfo (mirrors MOUNT_CLASS in level_quest.lua)</summary>
    private const int MountItemClass = 23;

    /// <summary>Bag slot holding our mount, or -1</summary>
    private int MountSlot(BotHandle handle)
    {
        if (handle.ZoneView is not { } zv || ClientData is null) return -1;
        int bestSlot = -1, bestLv = -1;
        foreach (var (slot, itemId) in zv.Inventory)
        {
            if (ClientData.Item(itemId) is not { } it) continue;
            if (it.Type != 1 || it.ItemClass != MountItemClass) continue;
            if (it.DemandLv > bestLv) { bestLv = it.DemandLv; bestSlot = slot; }
        }
        return bestSlot;
    }

    private async Task<bool> EnsureDismountedAsync(string id, BotHandle handle, CancellationToken ct)
    {
        // The Lua may have JUST sent a mount (its tick is independent of us) with the RIDE_ON ack still in flight — read…
        await Task.Delay(350, ct);
        if (handle.ZoneView is not { IsMounted: true }) return true;
        var slot = MountSlot(handle);
        if (slot < 0) { handle.Log("[travel] mounted but no mount item found in bag — cannot dismount for the gate"); return false; }
        handle.Log($"[travel] DISMOUNTING before gate (slot={slot}) — a gate is silently ignored while mounted");
        await UseItemAsync(id, (byte)slot, 0, ct);
        // 3000ms was set JUST UNDER the real dismount latency, so this "failed" essentially every time
        var ok = await WaitUntilAsync(
            () => handle.ZoneView?.IsMounted != true || handle.ZoneView is { ServerMenuOpen: true },
            8000, ct);
        if (ok && handle.ZoneView is { IsMounted: true, ServerMenuOpen: true })
            handle.Log(BotLogLevel.Info, "[travel] gate menu already open — not waiting out the dismount; " +
                                         "the RIDE_OFF arrives with the map transition");
        if (!ok) handle.Log("[travel] dismount NOT confirmed (no RIDE_OFF within 8s) — taking the gate anyway");
        return ok;
    }

    /// <summary>Send NC_ACT_STOP_REQ at our CURRENT position — no MOVERUN step</summary>
    private static async Task StopOnlyAsync(BotHandle handle, BotSession s, CancellationToken ct)
    {
        if (handle.Position is not { } pos) return;
        var stop = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), pos.X);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), pos.Y);
        await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
    }

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
        if (dist > 1) { handle.FacingDx = dx / dist; handle.FacingDy = dy / dist; }
    }

    /// <summary>Is a face-step actually REQUIRED to cast at ?</summary>
    private sealed record CastGeometry(double Dist, double Range, double OffByDeg, int ArcDeg, string Note);
    private CastGeometry _lastCastGeom = new(-1, -1, -1, 0, "none");

    private bool NeedsFacingAdjust(BotHandle handle, ushort skill, ushort target)
    {
        // A RECENT CONNECTING HIT IS PROOF we are in range AND faced — the server only lands our swing when both hold
        var recentHit = handle.ZoneView is { } zvh
            && zvh.LastRealDamageDealtAtUtc > DateTime.MinValue
            && (DateTime.UtcNow - zvh.LastRealDamageDealtAtUtc).TotalMilliseconds < 2500;
        var reachKnown = (ClientData?.Skill(skill)?.Range ?? 0) > 0
                         || (handle.ZoneView?.LearnedMeleeRange ?? 0) > 0;
        var canMeasure = reachKnown && NpcPos(handle, target) is not null && handle.Position is not null;
        if (recentHit && !canMeasure)
        { _lastCastGeom = new(-1, -1, -1, 0, "recent-hit bootstrap (reach/position unknown — cannot measure)"); return false; }

        if (NpcPos(handle, target) is not { } tp || handle.Position is not { } pos)
        { _lastCastGeom = new(-1, -1, -1, 0, "no target/self position"); return true; }
        var dx = (double)tp.X - pos.X; var dy = (double)tp.Y - pos.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1) { _lastCastGeom = new(dist, -1, -1, 0, "on top of target"); return false; }

        var si = ClientData?.Skill(skill);
        // Range 0 = melee skill; use the melee reach we already close to for auto-attack
        double range = si is { Range: > 0 } ? si.Range : (handle.ZoneView?.LearnedMeleeRange ?? 0);
        if (range <= 0) { _lastCastGeom = new(dist, 0, -1, 0, "melee reach NOT yet learned"); return true; }
        if (dist > range) { _lastCastGeom = new(dist, range, -1, si?.UsableDegree ?? 0, "OUT OF RANGE"); return true; }

        // Facing: compare our tracked heading against the direction to the target
        var deg = si?.UsableDegree ?? 0;
        if (deg <= 0) { _lastCastGeom = new(dist, range, -1, 0, "skill has no facing arc"); return false; }
        if (handle.FacingDx == 0 && handle.FacingDy == 0)
        { _lastCastGeom = new(dist, range, -1, deg, "HEADING UNKNOWN"); return true; }
        var dot = (handle.FacingDx * dx + handle.FacingDy * dy) / dist;
        dot = Math.Clamp(dot, -1.0, 1.0);
        var offBy = Math.Acos(dot) * 180.0 / Math.PI;
        var outside = offBy > deg / 2.0;
        _lastCastGeom = new(dist, range, offBy, deg, outside ? "OUTSIDE ARC" : "inside arc");
        return outside;                                   // outside the arc → must turn
    }

    /// <summary>Face ( , ) and STOP — a public wrapper over</summary>
    public async Task<ActionResult> CommitStopAsync(string id, uint x, uint y, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        await FaceAndStopAsync(handle, s, x, y, ct);
        return ActionResult.Sent;
    }

    /// <summary>Cast a heal skill on yourself (cast target = own handle)</summary>
    public Task<ActionResult> HealSelfAsync(string id, ushort skill, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return Task.FromResult(ActionResult.NotFound);
        if (handle.SelfHandle is not { } self) return Task.FromResult(ActionResult.NotInZone);
        return CastAsync(id, skill, self, stopFirst: null, ct: ct); // data-driven: heal is a moving-skill
    }

    /// <summary>Attack: cast a (damage) skill on , or the nearest non-gate mob in view when target is 0</summary>
    public Task<ActionResult> AttackAsync(string id, ushort skill, ushort target = 0, CancellationToken ct = default)
    {
        if (target == 0 && _bots.TryGetValue(id, out var h) && NearestMob(h) is { } m) target = m;
        if (target == 0) return Task.FromResult(ActionResult.NotFound);
        return CastAsync(id, skill, target, ct: ct); // data-driven face/stop from ActiveSkill
    }

    // STOP (ACT StopReq 0x2012): 8 bytes [x u32][y u32] — the position the char halts at
    private static readonly ushort OpActStop =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.StopReq);
    // ENDOFTRADE (ACT cmd 11 = 0x200B, empty): closes the current NPC shop/trade interaction
    private const ushort OpActEndOfTrade = (ushort)(((int)ProtocolCommand.Act << 10) | 11);
    // BASHSTART / BASHSTOP (BAT 0x242B / 0x2432, empty): begin / end melee auto-attack on the current target
    private const ushort OpBatBashStart = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.BashstartCmd);
    private const ushort OpBatBashStop = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.BashstopCmd);

    private const int TargetConfirmWaitMs = 60;

    private static async Task<bool> AwaitTargetConfirmAsync(BotHandle handle, ushort target, CancellationToken ct)
    {
        if (handle.ZoneView is not { } zv) return false;
        var deadline = DateTime.UtcNow.AddMilliseconds(TargetConfirmWaitMs);
        bool Confirmed() => zv.TargetConfirmedAtUtc > handle.TargetSetAtUtc && zv.TargetConfirmedHandle == target;
        while (DateTime.UtcNow < deadline && !Confirmed()) await Task.Delay(2, ct);
        return Confirmed();
    }

    /// <summary>Begin auto-attacking (melee swings) a target, or the nearest mob if 0</summary>
    public async Task<ActionResult> AutoAttackAsync(string id, ushort target = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        if (target == 0 && NearestMob(handle) is { } m) target = m;
        if (target == 0) return ActionResult.NotFound;

        if (handle.BashTarget == target
            && handle.LastBashSentUtc > DateTime.MinValue
            && (DateTime.UtcNow - handle.LastBashSentUtc).TotalMilliseconds < BashWindupMs)
        {
            handle.Log(BotLogLevel.Verbose,
                $"auto-attack h={target} SKIPPED — bashed {(DateTime.UtcNow - handle.LastBashSentUtc).TotalMilliseconds:F0}ms ago, still inside the windup");
            return ActionResult.Sent;
        }

        // BASHSTART carries NO target (payload 0b) — it swings at whatever the SERVER has selected
        var justTargeted = false;
        if (handle.CurrentTarget != target || !handle.TargetAsserted)
        {
            await s.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)target, (byte)(target >> 8) }), ct);
            handle.CurrentTarget = target; handle.TargetAsserted = true; handle.TargetSetAtUtc = DateTime.UtcNow;
            if (handle.ZoneView is { } zvT) zvT.CurrentTargetHandle = target;   // so a death can invalidate it
            justTargeted = true;
        }
        await EnsureBattleModeAsync(handle, s, ct);

        if (justTargeted)
        {
            var confirmed = await AwaitTargetConfirmAsync(handle, target, ct);
            handle.Log(BotLogLevel.Verbose,
                $"auto-attack h={target}: target {(confirmed ? "CONFIRMED" : "NOT confirmed")} after " +
                $"{(DateTime.UtcNow - handle.TargetSetAtUtc).TotalMilliseconds:F0}ms — " +
                $"{(confirmed ? "bashing" : "bashing anyway (timeout)")}");
        }

        var facedByRecentHit = handle.ZoneView is { } zvf
            && zvf.LastRealDamageDealtAtUtc > DateTime.MinValue
            && (DateTime.UtcNow - zvf.LastRealDamageDealtAtUtc).TotalMilliseconds < 2500;

        const double FaceOkDeg = 20.0;
        var facedByGeometry = false;
        double? bashAngleOff = null;
        if (handle.FacingDeg >= 0 && handle.Position is { } mypos && NpcPos(handle, target) is { } tgtpos)
        {
            var bear = Math.Atan2((double)tgtpos.Y - mypos.Y, (double)tgtpos.X - mypos.X) * 180.0 / Math.PI;
            if (bear < 0) bear += 360.0;
            bashAngleOff = Math.Abs(((bear - handle.FacingDeg + 540.0) % 360.0) - 180.0);
            facedByGeometry = bashAngleOff <= FaceOkDeg;
        }
        if (facedByRecentHit || facedByGeometry)
            await StopOnlyAsync(handle, s, ct);              // STOP without the swing-breaking MOVERUN
        else if (NpcPos(handle, target) is { } tp)
            await FaceAndStopAsync(handle, s, tp.X, tp.Y, ct);
        else
            await StopOnlyAsync(handle, s, ct);
        await s.SendAsync(new FiestaPacket(OpBatBashStart, Array.Empty<byte>()), ct);
        handle.LastBashSentUtc = DateTime.UtcNow;   // so a cast can tell "the swing has not started yet"
        // Remember WHAT we're bashing and that it's running, so the CEASE_FIRE handler can tell a cancelled swing stream…
        handle.BashTarget = target;
        if (handle.ZoneView is { } zvb) zvb.BashActive = true;
        // Decode -> log it in the same change: WHICH pre-bash path ran and the geometry that chose it, so "why did this…
        handle.Log(BotLogLevel.Verbose,
            $"auto-attack h={target} ({(facedByRecentHit ? "stop-only: recent hit" : facedByGeometry ? "stop-only: already faced" : "face-step+stop")}" +
            $", angleOff={(bashAngleOff is { } ao ? $"{ao:F0}deg" : "unknown")})");
        return ActionResult.Sent;
    }

    /// <summary>Stop auto-attacking (BAT BashstopCmd)</summary>
    public Task<ActionResult> StopAttackAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "stop auto-attack", s => s.SendAsync(new FiestaPacket(OpBatBashStop, Array.Empty<byte>()), ct));

    /// <summary>Position of a nearby entity by zone handle (null if not in view)</summary>
    private static (uint X, uint Y)? NpcPos(BotHandle handle, ushort target)
    {
        if (handle.ZoneView is not { } view) return null;
        foreach (var n in view.NearbyNpcs) if (n.Handle == target) return (n.X, n.Y);
        foreach (var p in view.NearbyPlayers) if (p.Handle == target) return (p.X, p.Y);
        return null;
    }

    /// <summary>Handle of the nearest huntable enemy to the bot (null if none in view)</summary>
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

    /// <summary>Number of mobs currently aggroing the bot (within the combat window) — the "am I overwhelmed?" signal a surviv…</summary>
    public int AggressorCount(string id)
        => _bots.TryGetValue(id, out var h) && h.ZoneView is { } v ? v.Aggressors.Count : 0;

    /// <summary>Flee: walk directly away from the threat (centroid of current aggressors, or the nearest mob) by world-units</summary>
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

    // Targeting / follow (zone) ───────────────────────────────────────────── Targeting and follow are zone-side
    private static readonly ushort OpBatUntarget =
        (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.UntargetReq);

    /// <summary>Target a zone handle</summary>
    public Task<ActionResult> TargetAsync(string id, ushort target, CancellationToken ct = default)
        => ActAsync(id, $"target h={target}",
            s => s.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)target, (byte)(target >> 8) }), ct));

    /// <summary>Clear the current target (Esc)</summary>
    public Task<ActionResult> UntargetAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "untarget", s => s.SendAsync(new FiestaPacket(OpBatUntarget, Array.Empty<byte>()), ct));

    /// <summary>Resolve a nearby player by name (case-insensitive) to their zone handle</summary>
    private static ushort? HandleForName(BotHandle handle, string name)
        => handle.ZoneView?.NearbyPlayers
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Handle;

    /// <summary>Follow a nearby player by name: target them, then chase by streaming MoverunCmd toward their live position (st…</summary>
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
                        // Re-plan when out of range and either the target moved enough since the last plan, or our last walk finished sh…
                        var moved = lastPlan is not { } lp
                            || Math.Abs((double)target.X - lp.X) + Math.Abs((double)target.Y - lp.Y) > followDist
                            || handle.WalkCts is null;
                        if (dist > followDist && moved)
                        {
                            var grid = handle.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
                            if (grid is not null)
                            {
                                // Pathfind around obstacles (a straight chase snags on lanterns/walls → MOVEFAIL), then walk it via WalkPath (ch…
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
                                // No grid available — one capped straight-line step
                                var step = Math.Min(dist - followDist, MaxStepFor(unitsPerSec));
                                var nx = (uint)Math.Round(pos.X + dx / dist * step);
                                var ny = (uint)Math.Round(pos.Y + dy / dist * step);
                                var p = new byte[16];
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), pos.X);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), pos.Y);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), nx);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), ny);
                                await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                                handle.CommitMove(pos.X, pos.Y, nx, ny);   // position AND facing
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

    /// <summary>Stop an in-progress loop (no-op if not following)</summary>
    public ActionResult StopFollow(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        handle.FollowCts?.Cancel();
        return ActionResult.Sent;
    }

    // Party (WorldManager link) ───────────────────────────────────────────── Party and friend traffic is WM-side, n…
    private const ushort OpPartyAllow = (ushort)(((int)ProtocolCommand.Party << 10) | 4); // 0x3804
    private const ushort OpPartyReject = (ushort)(((int)ProtocolCommand.Party << 10) | 5); // 0x3805
    // Incoming invite: the server asks the invitee with NC_PARTY_JOINPROPOSE_REQ (cmd 3, 0x3803) carrying the invite…
    private const ushort OpPartyJoinPropose = (ushort)(((int)ProtocolCommand.Party << 10) | 3); // 0x3803
    private const ushort OpPartyJoinCmd = (ushort)(((int)ProtocolCommand.Party << 10) | 8); // 0x3808 (joined)
    // Party MEMBER-STATE (the TEAMWORK foundation)
    private const ushort OpPartyMemberList     = (ushort)(((int)ProtocolCommand.Party << 10) | 9);  // 0x3809 roster + hp
    private const ushort OpPartyMemberInform   = (ushort)(((int)ProtocolCommand.Party << 10) | 50); // 0x3832 live hp/sp
    private const ushort OpPartyMemberClass    = (ushort)(((int)ProtocolCommand.Party << 10) | 51); // 0x3833 class/level/max
    private const ushort OpPartyMemberLocation = (ushort)(((int)ProtocolCommand.Party << 10) | 73); // 0x3849 positions
    private const ushort OpPartyLeaveCmd       = (ushort)(((int)ProtocolCommand.Party << 10) | 12); // 0x380C we left
    private const ushort OpPartyDismissCmd     = (ushort)(((int)ProtocolCommand.Party << 10) | 31); // 0x381F party disbanded
    // Incoming friend request: the server asks the bot to confirm with NC_FRIEND_SET_CONFIRM_REQ (Friend dept 0x15,…
    private const ushort OpFriendConfirmReq = (ushort)(((int)ProtocolCommand.Friend << 10) | 3); // 0x5403
    private const ushort OpFriendAddCmd = (ushort)(((int)ProtocolCommand.Friend << 10) | 8); // 0x5408

    /// <summary>Subscribe a BotHandle's WM link to track pending party invites + incoming friend requests (and clear them when…</summary>
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
                else if (IsPartyMemberStateOpcode(pkt.Opcode)) HandlePartyMemberState(handle, pkt.Opcode, pkt.Payload.Span);
                else if (pkt.Opcode == OpPartyLeaveCmd || pkt.Opcode == OpPartyDismissCmd)
                { handle.PartyMembers.Clear(); handle.Log("[party] left/dismissed — roster CLEARED"); }
                else if (pkt.Opcode == OpFriendConfirmReq)
                {
                    // In the CONFIRM_REQ the server swaps to the RECIPIENT's view: charid = us (the bot being asked), friendid = the…
                    var requester = FiestaText.Decode(pkt.ReadBody<PROTO_NC_FRIEND_SET_CONFIRM_REQ>().friendid.n5_name);
                    handle.PendingFriendRequester = requester;
                    handle.Log($"friend request from '{requester}' pending — friendConfirm to answer");
                }
                else if (pkt.Opcode == OpFriendAddCmd) handle.PendingFriendRequester = null; // added; resolved
            }
            catch { /* ignore an unparseable WM frame */ }
        };

    // Parse NC_PARTY_MEMBER_LIST_CMD(9)
    private static void ParsePartyRoster(BotHandle handle, ReadOnlySpan<byte> body)
    {
        if (body.Length < 1) return;
        int count = body[0], off = 1; const int SZ = 22;
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count && off + SZ <= body.Length; i++, off += SZ)
        {
            var name = FiestaText.Decode(body.Slice(off, 20).ToArray());
            if (string.IsNullOrWhiteSpace(name)) continue;
            seen.Add(name);
            handle.PartyMembers.GetOrAdd(name, n => new BotHandle.PartyMember { Name = n });
        }
        // prune anyone no longer in the roster (someone left/was kicked)
        foreach (var gone in handle.PartyMembers.Keys.Where(k => !seen.Contains(k)).ToList())
            handle.PartyMembers.TryRemove(gone, out _);
        handle.Log($"[party] MEMBER_LIST roster ({count}): {string.Join(", ", handle.PartyMembers.Keys)}");
    }

    private static bool IsPartyMemberStateOpcode(ushort op) =>
        op == OpPartyMemberInform || op == OpPartyMemberClass || op == OpPartyMemberLocation;

    // Parse the live member-state packets
    private static void HandlePartyMemberState(BotHandle handle, ushort op, ReadOnlySpan<byte> body)
    {
        if (body.Length < 1) return;
        static uint U32(ReadOnlySpan<byte> b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));
        int count = body[0], off = 1;
        int sz = op == OpPartyMemberInform ? 32 : op == OpPartyMemberClass ? 35 : 28; // 50 / 51 / 73
        for (int i = 0; i < count && off + sz <= body.Length; i++, off += sz)
        {
            var name = FiestaText.Decode(body.Slice(off, 20).ToArray());
            if (string.IsNullOrWhiteSpace(name)) continue;
            var m = handle.PartyMembers.GetOrAdd(name, n => new BotHandle.PartyMember { Name = n });
            if (op == OpPartyMemberInform) { m.Hp = U32(body, off + 20); m.Sp = U32(body, off + 24); }
            else if (op == OpPartyMemberClass)
            {
                m.ChrClass = body[off + 20]; m.Level = body[off + 21];
                m.MaxHp = U32(body, off + 22); m.MaxSp = U32(body, off + 26);
            }
            else { m.X = U32(body, off + 20); m.Y = U32(body, off + 24); } // MEMBERLOCATION(73)
        }
    }

    /// <summary>Invite to a party (WM link)</summary>
    public Task<ActionResult> PartyInviteAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"party invite {targetName}",
            s => s.SendAsync(new PROTO_NC_PARTY_JOIN_REQ { target = Name5Of(targetName) }, ct));

    /// <summary>Accept a pending party invite</summary>
    public Task<ActionResult> PartyAcceptAsync(string id, string? inviterName = null, CancellationToken ct = default)
    {
        var name = ResolveInviter(id, inviterName);
        if (name is null) return Task.FromResult(ActionResult.NotFound);
        return WmActAsync(id, $"party accept {name}",
            s => s.SendAsync(new FiestaPacket(OpPartyAllow, Name5Of(name).n5_name), ct))
            .ContinueWith(t => { if (_bots.TryGetValue(id, out var h)) h.PendingPartyInviter = null; return t.Result; });
    }

    /// <summary>Decline a pending party invite (tracked inviter if is null) — clears the stuck pending state</summary>
    public Task<ActionResult> PartyDeclineAsync(string id, string? inviterName = null, CancellationToken ct = default)
    {
        var name = ResolveInviter(id, inviterName);
        if (name is null) return Task.FromResult(ActionResult.NotFound);
        return WmActAsync(id, $"party decline {name}",
            s => s.SendAsync(new FiestaPacket(OpPartyReject, Name5Of(name).n5_name), ct))
            .ContinueWith(t => { if (_bots.TryGetValue(id, out var h)) h.PendingPartyInviter = null; return t.Result; });
    }

    /// <summary>The explicit inviter name, or the tracked pending one if none was given</summary>
    private string? ResolveInviter(string id, string? inviterName)
    {
        if (!string.IsNullOrWhiteSpace(inviterName)) return inviterName;
        return _bots.TryGetValue(id, out var h) && !string.IsNullOrWhiteSpace(h.PendingPartyInviter)
            ? h.PendingPartyInviter : null;
    }

    /// <summary>Send a line to party chat (WM link)</summary>
    public Task<ActionResult> PartyChatAsync(string id, string text, CancellationToken ct = default)
        => WmActAsync(id, $"party-chat: \"{text}\"", s => s.SendAsync(ChatCodec.BuildPartyChatReq(text), ct));

    // Friend list (WorldManager link) ─────────────────────────────────────── All friend structs carry [self charid…

    /// <summary>Send a friend request to (WM link)</summary>
    public Task<ActionResult> FriendAddAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"friend add {targetName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_SET_REQ { charid = Name5Of(self), friendid = Name5Of(targetName) }, ct));

    /// <summary>Answer an incoming friend request from : true = add, false = decline (WM link)</summary>
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

    /// <summary>Remove from the friend list (WM link)</summary>
    public Task<ActionResult> FriendDeleteAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"friend delete {targetName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_DEL_REQ { charid = Name5Of(self), friendid = Name5Of(targetName) }, ct));

    /// <summary>Build a 20-byte Name5 from a character name (ASCII, NUL-padded)</summary>
    private static Name5 Name5Of(string name)
    {
        var n5 = new Name5();
        var b = FiestaText.Encode(name);
        Array.Copy(b, n5.n5_name, Math.Min(b.Length, n5.n5_name.Length));
        return n5;
    }

    // Town multi-select portal (from Portals.pcapng): target the portal NPC, click it, then select a destination by…
    private const ushort OpActNpcClick = (ushort)(((int)ProtocolCommand.Act << 10) | 10);     // 0x200A
    private const ushort OpMapTownPortal = (ushort)(((int)ProtocolCommand.Map << 10) | 26);   // 0x181A

    /// <summary>Use a town multi-select portal: target → click the portal NPC → select destination (its TownPortal table index…</summary>
    public Task<ActionResult> TownPortalAsync(string id, ushort npcHandle, byte dest, CancellationToken ct = default)
        => ActAsync(id, $"town-portal via npc h={npcHandle} -> dest {dest}", async s =>
        {
            var hb = new byte[] { (byte)npcHandle, (byte)(npcHandle >> 8) };
            await s.SendAsync(new FiestaPacket(OpBatTarget, hb), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            await s.SendAsync(new FiestaPacket(OpMapTownPortal, new[] { dest }), ct);
        });

    // Field-gate link: a gate is an NPC (flagstate=1) with a handle
    private const ushort OpMapMultyLinkSelect = (ushort)(((int)ProtocolCommand.Map << 10) | 31); // 0x181F
    // SERVERMENU_ACK (Menu dept 0x0F, cmd 2): answers a server menu prompt (0x3C01)
    private const ushort OpMenuServerMenuAck = (ushort)((0x0F << 10) | 2); // 0x3C02
    // MAP_LOGINCOMPLETE (Map dept 6, cmd 3): "finished loading — spawn me in-world"
    private const ushort OpMapLoginComplete = (ushort)(((int)ProtocolCommand.Map << 10) | 3); // 0x1803

    /// <summary>Take a field gate by its NPC handle: target → NPC-click</summary>
    public async Task<ActionResult> UseGateAsync(string id, ushort gateHandle, string? destMap = null, byte menuOption = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        var hb = new byte[] { (byte)gateHandle, (byte)(gateHandle >> 8) };
        var view = handle.ZoneView;

        // An instance gate confirms with a menu (0x3C01 "move to ?") before it transitions you; a plain field gate trans…
        async Task AnswerMenu()
        {
            // Capture the parsed menu BEFORE answering (ClearServerMenu wipes it) so the trace shows exactly what we picked…
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

    /// <summary>Snapshot the gates the bot currently sees into the shared (auto-discovery): each in-view gate becomes an edge…</summary>
    private const long GateLearnSettleMs = 2500;

    public int ObserveGates(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return 0;
        if (handle.CurrentMap is not { } fromMap || handle.ZoneView is not { } view) return 0;
        // Block learning while the map is still settling after a transition (prevents the stale-CurrentMap mis-attributi…
        if (handle.MsSinceMapChange < GateLearnSettleMs) return 0;
        var n = 0;
        foreach (var gate in view.NearbyNpcs)
        {
            if (!gate.IsGate || string.IsNullOrWhiteSpace(gate.LinkMap)) continue;
            Graph.ObserveGate(fromMap, gate.LinkMap!, gate.X, gate.Y, gate.Handle);
            n++;
        }
        return n;
    }

    // Autonomous multi-map travel ─────────────────────────────────────────── Stop this far (world units) short of a…
    private const double GateApproachDist = 60.0;
    // In a scenario instance, an out-of-range cast target CLOSER than this is NOT client-approached — hold + autoAtt…
    private const double ScenarioHoldRange = 40.0;
    // How far SHORT of the target the instance combat-approach stops — closes into swing range without pathing onto…
    private const double ScenarioMeleeStop = 30.0;
    // TOO CLOSE (operator 2026-07-15): standing on top of a mob (~1u, from autoAttack's server-follow dragging us on…
    private const double ScenarioTooClose = 20.0;
    // How close a NearbyNpc must be to a town-portal's known coord to be taken as the portal NPC
    private const double PortalNpcRadius = 250.0;

    /// <summary>The live handle of the nearest in-view NPC (excluding field gates) to ( , ) within world-units, or null</summary>
    private static ushort? NearestNpcHandle(BotHandle handle, uint x, uint y, double maxDist)
    {
        var npcs = handle.ZoneView?.NearbyNpcs;
        if (npcs is null) return null;
        ushort? best = null; double bestD = maxDist;
        foreach (var n in npcs)
        {
            if (n.IsGate) continue; // a portal is a service NPC, not a field gate
            double d = Dist((n.X, n.Y), x, y);
            if (d <= bestD) { bestD = d; best = n.Handle; }
        }
        return best;
    }

    /// <summary>Outcome of kicking off an autonomous</summary>
    public enum TravelResult { Started, NotFound, NotInZone, AlreadyThere, NoRoute }

    /// <summary>Plan and begin autonomous travel to : route over the learned gate graph (BFS), then for each hop pathfind to t…</summary>
    public (TravelResult Result, IReadOnlyList<GateEdge>? Route) TravelTo(string id, string destMap, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return (TravelResult.NotFound, null);
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is null) return (TravelResult.NotInZone, null);
        if (handle.CurrentMap is not { } from) return (TravelResult.NotInZone, null);
        if (string.Equals(from, destMap, StringComparison.OrdinalIgnoreCase))
            return (TravelResult.AlreadyThere, Array.Empty<GateEdge>());

        // Seed the COMPLETE cross-map web from client nav data once (MapWayPoint/MapLinkPoint) plus the TOWN-PORTAL edge…
        SeedGraphIfNeeded(id);
        ObserveGates(id); // fold the bot's in-view gates into the graph (refreshes live handles)

        // Cost-gated route: Dijkstra over field gates AND town portals, edge cost = on-map walk distance to the transiti…
        var startPos = handle.Position is { } sp ? (sp.X, sp.Y) : (0u, 0u);
        var costed = Graph.RouteCost(from, startPos, destMap, null, (int)handle.Level, StraightLineCost);
        if (costed is not { Route.Count: > 0 } cr) return (TravelResult.NoRoute, null);
        var route = cr.Route;
        int portalHops = route.Count(e => e.IsPortal);
        handle.Log($"[travel] route to {destMap}: {route.Count} hop(s), {portalHops} portal, cost~{cr.Cost:F0} " +
                   $"[{string.Join(" -> ", route.Select(e => e.IsPortal ? $"{e.ToMap}(portal)" : e.ToMap))}]");

        var travelCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.TravelCts?.Cancel();
        handle.TravelCts = travelCts;
        handle.TravelDestMap = route[^1].ToMap;   // the END of the route, not the next hop
        _ = Task.Run(() => RunTravelAsync(handle, route, unitsPerSec, travelCts), travelCts.Token);
        return (TravelResult.Started, route);
    }

    /// <summary>Seed the cross-map graph from client nav data (field gates + town portals) once</summary>
    private void SeedGraphIfNeeded(string id)
    {
        if (Graph.Seeded || ClientData is not { } cd) return;
        var seedEdges = cd.BuildGateEdges();
        var n = Graph.Seed(seedEdges);
        var portalEdges = BuildPortalEdges(cd);
        Graph.SeedPortals(portalEdges);
        _bots.TryGetValue(id, out var hh);
        if (n == 0)
        {
            // An empty seed is a FAILURE to make it visible, not a quiet no-op: with no seeded web the bot can only route th…
            var why = cd.TableFailures.Count > 0
                ? " Failed client tables: " + string.Join(", ", cd.TableFailures.Select(kv => $"{kv.Key} ({kv.Value})"))
                : $" MapWayPoint/MapLinkPoint read clean from {cd.DataDir} but yielded no cross-map links.";
            hh?.Log(BotLogLevel.Note, "[travel] CRUTCH[CRIT] map graph seed produced ZERO gate edges — cross-map " +
                    "routing is limited to gates physically in view. Will retry on the next route request." + why);
            return;
        }
        hh?.Log($"[travel] seeded map graph: {n} gate edges + {portalEdges.Count} town-portal edges " +
                $"({Graph.Maps().Count} map nodes)");
    }

    /// <summary>Compute the cross-map route WITHOUT starting travel — a diagnostic / decision helper for the Lua leveler</summary>
    public (TravelResult Result, IReadOnlyList<GateEdge>? Route) RouteInfo(string id, string destMap)
    {
        if (!_bots.TryGetValue(id, out var handle)) return (TravelResult.NotFound, null);
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is null) return (TravelResult.NotInZone, null);
        if (handle.CurrentMap is not { } from) return (TravelResult.NotInZone, null);
        if (string.Equals(from, destMap, StringComparison.OrdinalIgnoreCase))
            return (TravelResult.AlreadyThere, Array.Empty<GateEdge>());
        SeedGraphIfNeeded(id);
        ObserveGates(id);
        var startPos = handle.Position is { } sp ? (sp.X, sp.Y) : (0u, 0u);
        var costed = Graph.RouteCost(from, startPos, destMap, null, (int)handle.Level, StraightLineCost);
        if (costed is not { Route.Count: > 0 } cr) return (TravelResult.NoRoute, null);
        return (TravelResult.Started, cr.Route);
    }

    /// <summary>Stop an in-progress (no-op if not travelling)</summary>
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

                // TOWN-PORTAL hop: walk to the portal NPC, resolve its live handle from view, then target+click it and select th…
                if (edge.IsPortal && edge.PortalDestIndex is int destIdx)
                {
                    if (edge.GateX != 0 || edge.GateY != 0)
                        await ApproachAsync(id, handle, edge.GateX, edge.GateY, GateApproachDist, unitsPerSec, ct);
                    if (!await WaitUntilAsync(() => NearestNpcHandle(handle, edge.GateX, edge.GateY, PortalNpcRadius) is not null, 8000, ct)
                        || NearestNpcHandle(handle, edge.GateX, edge.GateY, PortalNpcRadius) is not { } portalH)
                    {
                        handle.Log($"[travel] hop {hop + 1}/{route.Count}: no portal NPC in view near ({edge.GateX},{edge.GateY}) -> {expected} — aborting");
                        return;
                    }
                    handle.Log($"[travel] hop {hop + 1}/{route.Count}: -> {expected} via TOWN PORTAL npc h={portalH} dest={destIdx}");
                    await ApproachAsync(id, handle, edge.GateX, edge.GateY, GateApproachDist, unitsPerSec, ct);
                    handle.PendingDestMap = expected;
                    var pSeq = handle.MapChangeSeq;
                    await TownPortalAsync(id, portalH, (byte)destIdx, ct);
                    if (!await WaitUntilAsync(() => handle.MapChangeSeq > pSeq, 8000, ct))
                    {
                        handle.Log($"[travel] hop {hop + 1}: portal didn't fire from range — closing in and retrying");
                        await ApproachAsync(id, handle, edge.GateX, edge.GateY, 0, unitsPerSec, ct);
                        await TownPortalAsync(id, portalH, (byte)destIdx, ct);
                        if (!await WaitUntilAsync(() => handle.MapChangeSeq > pSeq, 8000, ct))
                        {
                            handle.Log($"[travel] hop {hop + 1}: portal failed twice — aborting");
                            handle.PendingDestMap = null; return;
                        }
                    }
                    handle.PendingDestMap = null;
                    if (!await WaitUntilAsync(
                            () => handle.Phase == BotPhase.InZone && handle.ZoneSession is not null
                                  && !handle.HandoffInFlight, 20000, ct))
                    {
                        handle.Log($"[travel] hop {hop + 1}: didn't re-enter zone after portal — aborting");
                        return;
                    }
                    handle.SetCurrentMap(expected, null, "travel-hop-expected");
                    ObserveGates(id);
                    handle.Log($"[travel] hop {hop + 1}/{route.Count}: arrived on {expected} (town portal)");
                    continue;
                }

                // Walk to the gate's KNOWN location first (seeded from MapWayPoint/MapLinkPoint, or last observed)
                if (edge.GateX != 0 || edge.GateY != 0)
                    await ApproachAsync(id, handle, edge.GateX, edge.GateY, GateApproachDist, unitsPerSec, ct);

                // Resolve the live gate to this destination from the current view (now that we're at its location)
                if (!await WaitUntilAsync(() => GateTo(handle, expected) is not null, 8000, ct)
                    || GateTo(handle, expected) is not { } gate)
                {
                    handle.Log($"[travel] hop {hop + 1}/{route.Count}: no gate to '{expected}' in view near ({edge.GateX},{edge.GateY}) — aborting");
                    // The edge is BOGUS/stale: we walked to its stored coord ON THIS MAP but the gate to `expected` isn't there (coo…
                    if (!edge.IsPortal && string.Equals(handle.CurrentMap, edge.FromMap, StringComparison.OrdinalIgnoreCase)
                        && Graph.RemoveEdge(edge.FromMap, edge.ToMap))
                        handle.Log($"[travel] PRUNED bogus gate edge {edge.FromMap} -> {edge.ToMap} @({edge.GateX},{edge.GateY}) (gate not there) — will re-route");
                    return;
                }
                handle.Log($"[travel] hop {hop + 1}/{route.Count}: -> {expected} via gate h={gate.Handle} @({gate.X},{gate.Y})");

                // Walk to within range of the gate (pathfind around obstacles if a grid is available; else a best-effort straigh…
                await ApproachAsync(id, handle, gate.X, gate.Y, GateApproachDist, unitsPerSec, ct);

                // Take the gate and wait for the transition
                handle.PendingDestMap = expected;
                var seqBefore = handle.MapChangeSeq;
                // A gate is silently ignored while mounted
                handle.SuppressMount = true;
                await EnsureDismountedAsync(id, handle, ct);
                await UseGateAsync(id, gate.Handle, ct: ct);
                if (!await WaitUntilAsync(() => handle.MapChangeSeq > seqBefore, 6000, ct))
                {
                    handle.Log($"[travel] hop {hop + 1}: gate didn't fire from range — closing in and retrying");
                    await ApproachAsync(id, handle, gate.X, gate.Y, 0, unitsPerSec, ct);
                    // Re-check: the approach can have re-mounted us, and a mounted gate-click is a no-op
                    await EnsureDismountedAsync(id, handle, ct);
                    await UseGateAsync(id, gate.Handle, ct: ct);
                    if (!await WaitUntilAsync(() => handle.MapChangeSeq > seqBefore, 8000, ct))
                    {
                        handle.Log($"[travel] hop {hop + 1}: no transition after retry — aborting");
                        handle.PendingDestMap = null;
                        handle.SuppressMount = false;   // re-allow transit mounting
                        return;
                    }
                }
                handle.PendingDestMap = null; // consumed by OnMapChanged
                handle.SuppressMount = false; // hop taken — transit mounting allowed again

                // A cross-server hop re-logs in on a fresh connection — wait until we're back in zone before the next hop.
                // Phase/ZoneSession alone are NOT enough: they still hold the OLD session's values while the teardown and
                // re-login run, so this wait used to pass ~2.3s early and the next hop's approach walked into a dead link.
                if (!await WaitUntilAsync(
                        () => handle.Phase == BotPhase.InZone && handle.ZoneSession is not null
                              && !handle.HandoffInFlight, 20000, ct))
                {
                    handle.Log($"[travel] hop {hop + 1}: didn't re-enter zone after handoff — aborting");
                    return;
                }
                handle.SetCurrentMap(expected, null, "travel-hop-expected"); // belt-and-suspenders for the next hop's grid
                ObserveGates(id); // learn the new map's gates (next hop + future routing)
                handle.Log($"[travel] hop {hop + 1}/{route.Count}: arrived on {expected}");
            }
            handle.Log(ct.IsCancellationRequested ? "[travel] cancelled" : $"[travel] done — arrived on {route[^1].ToMap}");
        }
        catch (OperationCanceledException) { handle.Log("[travel] cancelled"); }
        catch (Exception ex) { handle.Log($"[travel] error: {ex.Message}"); }
        finally
        {
            // Only clear if THIS travel still owns the slot: a newer TravelTo may already have replaced both, and
            // blanking its destination would make the UI claim the bot stopped travelling while it is still going.
            if (ReferenceEquals(handle.TravelCts, travelCts)) { handle.TravelCts = null; handle.TravelDestMap = null; }
            travelCts.Dispose();
        }
    }

    /// <summary>The live in-view gate whose link destination is (case-insensitive), or null if none is currently visible</summary>
    private static NearbyNpc? GateTo(BotHandle handle, string map)
        => handle.ZoneView?.NearbyNpcs.FirstOrDefault(
            n => n.IsGate && string.Equals(n.LinkMap, map, StringComparison.OrdinalIgnoreCase));

    /// <summary>Walk the bot to within world-units of ( , ), pathfinding over the current map's grid when one is available (a…</summary>
    private async Task ApproachAsync(string id, BotHandle handle, uint tx, uint ty, double stopShort, double unitsPerSec, CancellationToken ct)
    {
        if (handle.Position is not { } pos) return;
        var grid = handle.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
        IReadOnlyList<(uint X, uint Y)> wp;
        if (grid is not null)
        {
            var path = PathFinder.FindPath(grid, pos.X, pos.Y, tx, ty);
            if (path.Count == 0 && grid.RuntimeBlockedCount > 0)
            {
                // Unreachable on the runtime-augmented grid, but we hold learned MOVEFAIL blocks that may have wrongly SEVERED a…
                var poisoned = grid.RuntimeBlockedCount;
                grid.ClearRuntimeBlocked();
                path = PathFinder.FindPath(grid, pos.X, pos.Y, tx, ty);
                if (path.Count > 0)
                    handle.Log($"[nav] approach to ({tx},{ty}) was UNREACHABLE — cleared {poisoned} poisoned learned-blocks, route re-opened");
            }
            if (path.Count == 0)
            {
                // Genuinely disconnected on the base grid too (FindPath snaps a blocked start/goal to nearest walkable, so empty…
                var (stx, sty) = grid.WorldToTile(pos.X, pos.Y);
                var (gtx, gty) = grid.WorldToTile(tx, ty);
                handle.Log($"[nav] approach to ({tx},{ty}) UNREACHABLE on {handle.CurrentMap} grid — aborting " +
                    $"(pos=({pos.X},{pos.Y}) tile=({stx},{sty})walk={grid.IsWalkableTile(stx, sty)} " +
                    $"goalTile=({gtx},{gty})walk={grid.IsWalkableTile(gtx, gty)} " +
                    $"grid={grid.WidthTiles}x{grid.HeightTiles} rtBlocked={grid.RuntimeBlockedCount})");
                return;
            }
            wp = PathFinder.Simplify(path);
        }
        else wp = new[] { (pos.X, pos.Y), (tx, ty) }; // no grid → best-effort direct

        // Trim trailing waypoints inside stopShort of the target so we halt short of the gate
        if (stopShort > 0 && wp.Count > 2)
        {
            var keep = wp.Count;
            while (keep > 2 && Dist(wp[keep - 1], tx, ty) < stopShort) keep--;
            wp = wp.Take(keep).ToList();
        }
        WalkPath(id, wp, unitsPerSec);
        double pathLen = 0;
        for (int i = 1; i < wp.Count; i++) pathLen += Dist(wp[i - 1], wp[i].X, wp[i].Y);
        var speed = handle.WalkSpeed > 0 ? handle.WalkSpeed : unitsPerSec;
        int waitMs = (int)Math.Clamp(pathLen / Math.Max(1, speed) * 1000 * 1.6 + 10000, 30000, 180000);
        // A WALK THAT NEVER STARTED MUST NOT COST THE FULL TIMEOUT. That timeout is sized for the walk to COMPLETE --
        // at minimum 30s and up to 3 minutes -- so if the movement was never delivered (the classic case: sent into a
        // link that was being torn down by a handoff) the bot stands still for the whole of it, doing nothing, and
        // only the liveness watchdog eventually frees it. That is the "randomly standing still" the operator sees.
        // Detect the no-start case in seconds and hand back to the caller, which has its own re-approach path.
        var origin = handle.Position;
        var startedAt = Environment.TickCount64;
        var neverMoved = false;
        await WaitUntilAsync(() =>
        {
            if (handle.Position is { } p)
            {
                if (Dist((p.X, p.Y), tx, ty) <= Math.Max(stopShort, 24)) return true;
                if (origin is { } o && Environment.TickCount64 - startedAt > ApproachNoStartMs
                    && Dist((p.X, p.Y), o.X, o.Y) < ApproachMovedEps)
                {
                    neverMoved = true;
                    return true;
                }
            }
            return handle.WalkCts is null;
        }, waitMs, ct);
        if (neverMoved)
            handle.Log($"[nav] approach to ({tx},{ty}): the walk NEVER STARTED — still within {ApproachMovedEps}u of " +
                       $"({origin?.X},{origin?.Y}) after {ApproachNoStartMs}ms, {wp.Count} waypoint(s) issued. " +
                       "Returning instead of holding the full arrival timeout; the caller re-approaches.");
    }

    /// <summary>How long to give a freshly-issued walk to produce ANY movement before calling it a no-start</summary>
    private const int ApproachNoStartMs = 6000;
    /// <summary>World units of drift still counted as "has not moved" (server position updates jitter a little)</summary>
    private const double ApproachMovedEps = 8.0;

    private static double Dist((uint X, uint Y) a, uint x, uint y)
        => Math.Sqrt(Math.Pow((double)a.X - x, 2) + Math.Pow((double)a.Y - y, 2));

    /// <summary>Straight-line (Euclidean) world-unit distance — the routing cost proxy for an on-map walk between two points (…</summary>
    private static double StraightLineCost((uint X, uint Y) a, (uint X, uint Y) b)
        => Math.Sqrt(Math.Pow((double)a.X - b.X, 2) + Math.Pow((double)a.Y - b.Y, 2));

    /// <summary>Build the town-portal routing edges from TownPortal.shn : within each portal group, every member map links to…</summary>
    private static IReadOnlyList<GateEdge> BuildPortalEdges(GameData.ClientData cd)
    {
        // ONE TOWN-GATE NETWORK, NOT ONE PER `TP_GroupNo`
        var edges = new List<GateEdge>();
        var all = cd.BuildPortalDests().ToList();
        foreach (var a in all)
            foreach (var b in all)
            {
                if (a.Index == b.Index || string.Equals(a.Map, b.Map, StringComparison.OrdinalIgnoreCase)) continue;
                edges.Add(new GateEdge(a.Map, b.Map, a.X, a.Y, 0,
                    PortalDestIndex: b.Index, MinLevel: b.MinLevel, ToX: b.X, ToY: b.Y));
            }
        return edges;
    }

    /// <summary>Poll until it's true or elapses; returns the final state</summary>
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

    // Soul-stone HP/SP recharge (SOULSTONE dept 20: HP_USE_REQ cmd 7 = 0x5007, SP_USE_REQ cmd 9 = 0x5009; both empty…
    private const ushort OpSoulStoneHpUse = 0x5007;
    private const ushort OpSoulStoneSpUse = 0x5009;

    /// <summary>Recharge current SP from the character's SP soul-stone reserve (NC_SOULSTONE_SP_USE_REQ) — the in-game "use an…</summary>
    public Task<ActionResult> UseSoulStoneSpAsync(string id, CancellationToken ct = default)
    {
        // Don't USE at full SP — the server rejects it (USEFAIL), wasting the call and (worse) looking like an empty res…
        if (_bots.TryGetValue(id, out var h) && h.ZoneView is { } v && v.Sp is { } sp && v.MaxSp > 0 && sp >= v.MaxSp)
            return Task.FromResult(ActionResult.Sent);
        return ActAsync(id, "soul-stone SP recharge (0x5009)", s =>
        {
            // Note the kind BEFORE the reply can arrive: USEFAIL (0x5006) is shared HP+SP and only this correlation lets Zon…
            if (_bots.TryGetValue(id, out var hh)) hh.ZoneView?.NoteStoneUseFired(hp: false);
            return s.SendAsync(new FiestaPacket(OpSoulStoneSpUse, ReadOnlyMemory<byte>.Empty), ct);
        });
    }

    /// <summary>Recharge current HP from the character's HP soul-stone reserve (NC_SOULSTONE_HP_USE_REQ) — the in-game "use an…</summary>
    public Task<ActionResult> UseSoulStoneHpAsync(string id, CancellationToken ct = default)
    {
        if (_bots.TryGetValue(id, out var h) && h.ZoneView is { } v && v.Hp is { } hp && v.MaxHp > 0 && hp >= v.MaxHp)
            return Task.FromResult(ActionResult.Sent);
        return ActAsync(id, "soul-stone HP USE sent (0x5007) — awaiting ack", s =>
        {
            // Same correlation as the SP use above — USEFAIL carries no HP/SP marker
            if (_bots.TryGetValue(id, out var hh)) hh.ZoneView?.NoteStoneUseFired(hp: true);
            return s.SendAsync(new FiestaPacket(OpSoulStoneHpUse, ReadOnlyMemory<byte>.Empty), ct);
        });
    }

    // Shop / buy. Clicking a merchant (target → NPCClick) makes the server send the SHOPOPEN list (decoded by ZoneVi…
    private const ushort OpActNpcMenuAck = (ushort)(((int)ProtocolCommand.Act << 10) | 29); // 0x201D

    /// <summary>Whether a quest-dialogue page's TEXT (QuestDialog.shn, looked up by ) carries the [MENU] tag — the data-driven…</summary>
    private bool DialogHasMenuTag(int dialogId)
        => ClientData?.QuestDialog(dialogId)?.Contains("[MENU]", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>True when quest is accepted by pressing a MENU BUTTON rather than a plain NC_QUEST_SELECT_START_REQ : its STAR…</summary>
    private bool StartAcceptIsButton(int questId)
    {
        // No 0-as-"none" guard: callers pass a real id (the nullable is unwrapped first)
        if (ClientData?.Quest(questId) is not { } q) return false;
        var dlg = ClientData.QuestDialog(q.StartDialogId);
        return dlg is not null && dlg.Contains("[BUTTON]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Open a merchant's shop and wait for its sell list</summary>
    /// <summary>Open an NPC's shop. <paramref name="maxReclicks"/>=1 is PROBE mode: one click, one 2s wait.
    /// The 8-reclick default is for "I know this is a shop, open it"; using it to ASK "is this a shop?" costs
    /// 16s per NPC that never replies, which is ~13 min per town of ~50 NPCs.</summary>
    public async Task<ActionResult> OpenShopAsync(string id, ushort npcHandle, byte menuOption = 1, CancellationToken ct = default, int maxReclicks = 8)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        var view = handle.ZoneView;
        var hb = new byte[] { (byte)npcHandle, (byte)(npcHandle >> 8) };
        // SYNC request→response open (operator 2026-06-30: "the recency window is insane — sync call with a timeout")
        var requestedMenu = false;
        for (var reclicks = 0; reclicks < Math.Max(1, maxReclicks); reclicks++)
        {
            view?.ResetShopState();
            view?.ClearNpcMenu();
            view?.ClearQuestScript();
            await s.SendAsync(new FiestaPacket(OpActEndOfTrade, ReadOnlyMemory<byte>.Empty), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            if (handle.Position is { } pos)
            {
                var stop = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), pos.X);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), pos.Y);
                await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
            }
            requestedMenu = false;
            for (var waited = 0; waited < 2000; waited += 50)
            {
                if (view?.ShopOpen == true)
                {
                    handle.Log($"open shop npc h={npcHandle} OPEN ({ShopKindStr(view.LastShopKind)})");
                    return ActionResult.Sent;
                }
                if (view is { } v && v.RandomOptionUtc > DateTime.MinValue)
                {
                    // RandomOption (Anvil reforge) — NOT a shop
                    await s.SendAsync(new FiestaPacket(OpActEndOfTrade, ReadOnlyMemory<byte>.Empty), ct);
                    handle.Log($"open shop npc h={npcHandle} — RandomOption menu, NOT a shop — closed, ignoring NPC");
                    return ActionResult.Sent;
                }
                if (view?.DequeueQuestStep() is { } step)
                {
                    waited = 0; // progress — reset the stall clock, keep waiting on THIS click
                    if (step.Qsc is 0x06 or 0x0A) continue; // terminal — not ours to act on here
                    if (DialogHasMenuTag(step.DialogId))
                    {
                        if (!requestedMenu)
                        {
                            requestedMenu = true;
                            await s.SendAsync(new PROTO_NC_ACT_NPCMENUOPEN_ACK { ack = menuOption }, ct);
                            handle.Log($"open shop npc h={npcHandle} — q{step.QuestId} page has [MENU], requesting shop instead of acking it");
                        }
                    }
                    else
                    {
                        await AnswerQuestAsync(id, step.QuestId, step.Qsc, 1, ct);
                        handle.Log($"open shop npc h={npcHandle} — q{step.QuestId} page has no bypass, acked (Next) to progress");
                    }
                    continue;
                }
                if (!requestedMenu && view?.NpcMenuOpen == true)
                {
                    await s.SendAsync(new PROTO_NC_ACT_NPCMENUOPEN_ACK { ack = menuOption }, ct);
                    view?.ClearNpcMenu();
                    requestedMenu = true;
                }
                await Task.Delay(50, ct);
            }
            handle.Log($"open shop npc h={npcHandle} — no shop reply (re-click {reclicks + 1}/8)");
        }
        // No tracked reply after repeated re-clicks — close anything left open and treat as not-a-shop
        await s.SendAsync(new FiestaPacket(OpActEndOfTrade, ReadOnlyMemory<byte>.Empty), ct);
        handle.Log($"open shop npc h={npcHandle} — not a shop (timeout/untracked) — ignoring NPC");
        return ActionResult.Sent;
    }

    private static string ShopKindStr(Session.ShopKind k) => k switch
    {
        Session.ShopKind.Skill => "skill",
        Session.ShopKind.Weapon => "weapon",
        Session.ShopKind.Item => "item",
        Session.ShopKind.SoulStone => "soulstone",
        Session.ShopKind.Storage => "storage",   // personal warehouse (0x3C08) — a role, not a shop
        _ => "unknown",
    };

    /// <summary>Sell of the bag item at to the open shop (NC_ITEM_SELL_REQ {slot, lot})</summary>
    public Task<ActionResult> SellAsync(string id, byte slot, uint lot, CancellationToken ct = default)
        => ActAsync(id, $"sell slot {slot} x{lot}",
            s => s.SendAsync(new PROTO_NC_ITEM_SELL_REQ { slot = slot, lot = lot }, ct));

    /// <summary>Move an item between the BAG and personal STORAGE, and VERIFY it landed</summary>
    public async Task<ActionResult> StorageMoveAsync(string id, byte fromSlot, byte toSlot,
        bool deposit = true, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        var view = handle.ZoneView;
        if (view is null) return ActionResult.NotInZone;
        if (!view.StorageOpen)
        {
            handle.Log(BotLogLevel.Note, $"CRUTCH[CRIT] storage {(deposit ? "deposit" : "withdraw")} REFUSED — no storage session is open " +
                                         "(never fire a move into a closed storage; that is the silent no-op we must not have)");
            return ActionResult.NotInZone;
        }
        var storageBox = view.StorageBox;
        var from = (ushort)((deposit ? MainBagBox : storageBox) << 10 | fromSlot);
        var to = (ushort)((deposit ? storageBox : MainBagBox) << 10 | toSlot);

        var before = view.CellChangeCount;
        var ackBefore = view.LastRelocAckAtUtc;   // so we report THIS move's ack, never a stale one
        // What the SOURCE bag cell holds right now
        var srcHadItem = view.Inventory.TryGetValue(fromSlot, out var srcItem);
        await s.SendAsync(new PROTO_NC_ITEM_RELOC_REQ
        {
            from = new FiestaLibReloaded.Networking.Structs.ITEM_INVEN { Inven = from },
            to = new FiestaLibReloaded.Networking.Structs.ITEM_INVEN { Inven = to },
        }, ct);
        // Wait for the server's CELLCHANGE pair
        for (var waited = 0; waited < 3000; waited += 50)
        {
            // "ANY CELLCHANGE HAPPENED" IS NOT PROOF THIS MOVE HAPPENED
            var confirmed = deposit
                ? srcHadItem && !(view.Inventory.TryGetValue(fromSlot, out var nowItem) && nowItem == srcItem)
                : view.CellChangeCount > before;
            if (confirmed)
            {
                handle.Log(BotLogLevel.Note, $"storage {(deposit ? "DEPOSIT" : "WITHDRAW")} ok: " +
                    $"box{from >> 10} slot{from & 0xFF} -> box{to >> 10} slot{to & 0xFF}" +
                    (deposit ? " (bag cell cleared)" : ""));
                return ActionResult.Sent;
            }
            await Task.Delay(50, ct);
        }
        // A deposit whose source cell never cleared is a FAILURE even if cells changed elsewhere
        if (deposit && view.CellChangeCount > before)
            handle.Log(BotLogLevel.Note, $"storage DEPOSIT: cells DID change but bag slot {fromSlot} still holds " +
                $"item {srcItem} — the move did not free the slot (this is what the old any-cellchange check mis-read as success)");
        // The server DOES answer every RELOC with NC_ITEM_RELOC_ACK (0x300C) — we were just throwing it away, so this li…
        var ackTxt = view.LastRelocAckAtUtc > ackBefore
            ? $"server answered RELOC_ACK code={view.LastRelocAckCode} (0x{view.LastRelocAckCode:X4}) — the move was REFUSED, not lost"
            : "and NO RELOC_ACK either — the request itself never landed";
        handle.Log(BotLogLevel.Note, $"CRUTCH[CRIT] storage {(deposit ? "DEPOSIT" : "WITHDRAW")} FAILED — no CELLCHANGE in 3s for " +
            $"box{from >> 10} slot{from & 0xFF} -> box{to >> 10} slot{to & 0xFF}: {ackTxt}");
        return ActionResult.NotInZone;
    }

    private const int MainBagBox = 9;   // matches ZoneView's MainBag — the bag half of a storage move

    /// <summary>Enchant (upgrade) the gear in equip slot using the enhancement stones at the given inventory slots (NC_ITEM_UP…</summary>
    public Task<ActionResult> EnchantAsync(string id, byte equip, byte raw,
        byte rawLeft = 0xFF, byte rawMiddle = 0xFF, byte rawRight = 0xFF, uint money = 0, CancellationToken ct = default)
        => ActAsync(id, $"enchant equip {equip} (raw={raw} l={rawLeft} m={rawMiddle} r={rawRight})",
            s => s.SendAsync(new PROTO_NC_ITEM_UPGRADE_REQ
            {
                equip = equip, raw = raw, raw_left = rawLeft, raw_middle = rawMiddle, raw_right = rawRight, gift_money = money
            }, ct));

    /// <summary>Buy HP soul-stone charges (NC_SOULSTONE_HP_BUY_REQ 0x5001) into the reserve that draws from</summary>
    public Task<ActionResult> BuyHpStoneAsync(string id, ushort number, CancellationToken ct = default)
        => ActAsync(id, $"buy HP soul-stone x{number}",
            s => s.SendAsync(new PROTO_NC_SOULSTONE_HP_BUY_REQ { number = number }, ct));

    /// <summary>Buy SP soul-stone charges (NC_SOULSTONE_SP_BUY_REQ 0x5002)</summary>
    public Task<ActionResult> BuySpStoneAsync(string id, ushort number, CancellationToken ct = default)
        => ActAsync(id, $"buy SP soul-stone x{number}",
            s => s.SendAsync(new PROTO_NC_SOULSTONE_SP_BUY_REQ { number = number }, ct));

    // NC_CHAR_REVIVE_REQ (Char cmd 78 = 0x104E): "move to respawn point" -> nearest town, answered after death (DEAD…
    private const ushort OpCharReviveReq = 0x104E;

    /// <summary>Respawn after death — "move to respawn point" (NC_CHAR_REVIVE_REQ), which returns the char to the nearest town…</summary>
    public Task<ActionResult> RespawnAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "respawn (move to respawn point -> nearest town)",
            s => s.SendAsync(new FiestaPacket(OpCharReviveReq, ReadOnlyMemory<byte>.Empty), ct));

    /// <summary>Buy of item from the currently-open shop (NC_ITEM_BUY_REQ)</summary>
    public Task<ActionResult> BuyAsync(string id, ushort itemId, uint lot, CancellationToken ct = default)
        => ActAsync(id, $"buy item {itemId} x{lot}",
            s => s.SendAsync(new PROTO_NC_ITEM_BUY_REQ { itemid = itemId, lot = lot }, ct));

    // Quests ──────────────────────────────────────────────────────────────── Click an NPC (NC_ACT_NPCCLICK_CMD) — s…
    public Task<ActionResult> ClickNpcAsync(string id, ushort npcHandle, CancellationToken ct = default)
        => ActAsync(id, $"click npc h={npcHandle}",
            s => s.SendAsync(new FiestaPacket(OpActNpcClick, new[] { (byte)npcHandle, (byte)(npcHandle >> 8) }), ct));

    /// <summary>Answer a quest-dialogue step (NC_QUEST_SCRIPT_CMD_ACK)</summary>
    public Task<ActionResult> AnswerQuestAsync(string id, ushort questId, byte qsc, uint result = 1, CancellationToken ct = default)
        => ActAsync(id, $"quest {questId} answer qsc=0x{qsc:X2} result={result}",
            s => s.SendAsync(new PROTO_NC_QUEST_SCRIPT_CMD_ACK { nQuestID = questId, nQSC = qsc, nResult = result }, ct));

    /// <summary>Abandon a quest (NC_QUEST_GIVE_UP_REQ {questId})</summary>
    public Task<ActionResult> GiveUpQuestAsync(string id, ushort questId, CancellationToken ct = default)
        => ActAsync(id, $"quest {questId} give up",
            s => s.SendAsync(new PROTO_NC_QUEST_GIVE_UP_REQ { nQuestID = questId }, ct));

    /// <summary>Start a quest by id (NC_QUEST_START_REQ {questId})</summary>
    public Task<ActionResult> StartQuestAsync(string id, ushort questId, CancellationToken ct = default)
    {
        // Stash the questId so the questId-less NC_QUEST_START_ACK can be attributed to it
        if (_bots.TryGetValue(id, out var h)) h.ZoneView?.NoteQuestStartAttempt(questId);
        return ActAsync(id, $"quest {questId} start req",
            s => s.SendAsync(new PROTO_NC_QUEST_START_REQ { nQuestID = questId }, ct));
    }

    /// <summary>ACCEPT A QUEST REMOTELY from the quest log — no travelling to the giver, no NPC click</summary>
    public async Task<ActionResult> RemoteAcceptQuestAsync(string id, ushort questId, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var h)) return ActionResult.NotFound;
        if (h.Phase != BotPhase.InZone) return ActionResult.NotInZone;
        if (ClientData?.Quest(questId) is { } qd && !qd.RemoteAcceptable)
        {
            h.Log(BotLogLevel.Note, $"quest {questId} REMOTE-ACCEPT refused — not flagged remotely acceptable (@25); travel to the giver instead");
            return ActionResult.NotFound;
        }
        var start = await StartQuestAsync(id, questId, ct);
        if (start != ActionResult.Sent) return start;
        // Drain the pages START_REQ triggered
        await DriveQuestDialogueAsync(id, npcHandle: null, questId: questId, ct: ct);   // null = REMOTE (no NPC)
        var ok = h.ZoneView?.IsQuestActive(questId) == true;
        h.Log(BotLogLevel.Note, ok
            ? $"quest {questId} REMOTE-ACCEPTED from the quest log (no travel to the giver)"
            : $"CRUTCH[WARN] quest {questId} remote-accept did NOT take (quest not active after the drain) — falling back to travelling to the giver");
        return ok ? ActionResult.Sent : ActionResult.NotFound;
    }

    /// <summary>Answer the currently-pending quest dialogue step (from ) — "proceed"</summary>
    public Task<ActionResult> ProceedQuestAsync(string id, uint result = 1, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var h) || h.ZoneView?.PendingQuest is not { } q)
            return Task.FromResult(ActionResult.NotFound);
        return AnswerQuestAsync(id, q.QuestId, q.Qsc, result, ct);
    }

    /// <summary>Drive a whole quest dialogue: click the NPC, then ACK each server-pushed script page (NC_QUEST_SCRIPT_CMD_REQ)…</summary>
    public async Task<ActionResult> DriveQuestDialogueAsync(string id, ushort? npcHandle, uint result = 1, int rewardIndex = -1, int maxSteps = 24, ushort? questId = null, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var h)) return ActionResult.NotFound;
        if (h.Phase != BotPhase.InZone || h.ZoneSession is not { } s) return ActionResult.NotInZone;
        var zv = h.ZoneView;

        // Baseline on the currently-pending step so we only answer pages that arrive AFTER this click (a stale step from…
        var lastSeen = zv?.PendingQuest?.AtUtc ?? DateTime.MinValue;
        zv?.ClearRewardSelect();
        zv?.ClearNpcMenu();
        zv?.ClearQuestScript();   // drop stale pages so we drain ONLY the burst this click triggers
        // CLOSE any shop/trade UI still open from a PRECEDING interaction
        if (npcHandle is { } clickHandle)
        {
            await s.SendAsync(new FiestaPacket(OpActEndOfTrade, ReadOnlyMemory<byte>.Empty), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, new[] { (byte)clickHandle, (byte)(clickHandle >> 8) }), ct);
        }
        // The real client ALWAYS follows NPCCLICK with STOP_REQ (0x2012) reporting the position it halted at to talk — a…
        if (npcHandle is not null && h.Position is { } pos)
        {
            var stop = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), pos.X);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), pos.Y);
            await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
        }
        h.Log(npcHandle is null
            ? $"quest dialogue: REMOTE (no npc click) — draining the pages START_REQ already triggered (result={result}, rewardIndex={rewardIndex})"
            : $"quest dialogue: click npc h={npcHandle} + stop, driving (result={result}, rewardIndex={rewardIndex})");

        // A quest GIVER that offers SEVERAL quests opens an NPC MENU (0x201C) on click — the quest script (0x4401) only…
        for (var w = 0; w < 1500
             && zv?.NpcMenuOpen != true
             && (zv?.PendingQuest?.AtUtc ?? DateTime.MinValue) <= lastSeen; w += 80)
            await Task.Delay(80, ct);
        // A REMOTE drive never opens an NPC menu (there was no click) — skip the whole menu branch
        if (npcHandle is not null && zv?.NpcMenuOpen == true)
        {
            if (questId is { } selQid && !StartAcceptIsButton(selQid))
            {
                var npcId = zv.MenuNpcId != 0 ? zv.MenuNpcId : (ushort)(zv.NearbyNpcs.FirstOrDefault(n => n.Handle == npcHandle)?.MobId ?? 0);
                await s.SendAsync(new PROTO_NC_QUEST_SELECT_START_REQ { nNPCID = npcId, nQuestID = selQid }, ct);
                zv.ClearNpcMenu();
                h.Log($"quest dialogue: SELECT_START npc={npcId} quest={selQid} (multi-quest menu)");
            }
            else if (questId is { } btnQid && StartAcceptIsButton(btnQid))
            {
                // BUTTON/[MENU]-accept quest
                await s.SendAsync(new PROTO_NC_ACT_NPCMENUOPEN_ACK { ack = 1 }, ct);
                zv.ClearNpcMenu();
                h.Log($"quest dialogue: BUTTON-accept q{btnQid} — answered NPC menu (option 1 = the accept button) npc={zv.MenuNpcId}");
            }
            else
            {
                // No specific quest given → answer the menu with option 1 (NPCMENUOPEN_ACK) to reach the quest dialogue
                await s.SendAsync(new PROTO_NC_ACT_NPCMENUOPEN_ACK { ack = 1 }, ct);
                zv.ClearNpcMenu();
                h.Log($"quest dialogue: answered NPC menu (option 1) npc={zv.MenuNpcId} to reach the quest dialogue");
            }
        }

        // DRAIN the server's script-page BURST and ACK each page — the real client acks EVERY SAY page (0x4402 {questId,…
        var menuAnswers = questId is { } mqid ? DeriveMenuAnswers(mqid) : new Dictionary<int, uint>();
        if (menuAnswers.Count > 0)
            h.Log($"quest {questId}: derived menu answers {string.Join(",", menuAnswers.Select(kv => $"say{kv.Key}=>{kv.Value}"))}");
        bool rewardSelected = false, concluded = false, redirected = false;
        int answered = 0;
        var idle = DateTime.UtcNow.AddSeconds(3.0); // wait for the first page; refreshed as pages arrive
        while (DateTime.UtcNow < idle && answered < maxSteps && !concluded)
        {
            var cur = zv?.DequeueQuestStep();
            if (cur is null) { await Task.Delay(50, ct); continue; }
            idle = DateTime.UtcNow.AddSeconds(2.0); // got a page → keep draining the rest of the burst
            lastSeen = cur.AtUtc;
            // A page for a DIFFERENT quest than the one we're after is continuation NOISE from some OTHER active quest at th…
            if (questId is { } wantQid && cur.QuestId != wantQid)
            {
                if (cur.Qsc is 0x06 or 0x0A) continue; // someone else's terminal — nothing to act on
                if (DialogHasMenuTag(cur.DialogId))
                {
                    if (!redirected)
                    {
                        redirected = true;
                        var npcId = zv?.MenuNpcId != 0 ? zv!.MenuNpcId : (ushort)(zv?.NearbyNpcs.FirstOrDefault(n => n.Handle == npcHandle)?.MobId ?? 0);
                        await s.SendAsync(new PROTO_NC_QUEST_SELECT_START_REQ { nNPCID = npcId, nQuestID = wantQid }, ct);
                        h.Log($"quest dialogue: npc h={npcHandle} showed q{cur.QuestId} [MENU] page (not acked) — we want q{wantQid}, SELECT_START npc={npcId}");
                    }
                }
                else
                {
                    await AnswerQuestAsync(id, cur.QuestId, cur.Qsc, 1, ct);
                    h.Log($"quest dialogue: npc h={npcHandle} showed q{cur.QuestId} (no bypass) — acked (Next) to progress toward q{questId}");
                }
                continue;
            }
            if (cur.Qsc is 0x06 or 0x0A) { concluded = true; break; } // ACCEPT/DONE — terminal, no ack
            // On the [SHOW_REWARD] page (the [Complete the Quest] button), pick the reward BEFORE acking it — the real clien…
            if (rewardIndex >= 0 && !rewardSelected &&
                (ClientData?.QuestDialog(cur.DialogId)?.Contains("SHOW_REWARD", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                await SelectQuestRewardAsync(id, cur.QuestId, (uint)rewardIndex, ct);
                rewardSelected = true;
                h.Log($"quest dialogue: reward-select quest={cur.QuestId} index={rewardIndex} (SHOW_REWARD dlg {cur.DialogId})");
            }
            // Per-page answer, DERIVED from the quest's own script (menuAnswers, keyed by dialogId): a plain SAY/continue pa…
            uint pageResult = menuAnswers.TryGetValue((int)cur.DialogId, out var derived) ? derived : 1u;
            await AnswerQuestAsync(id, cur.QuestId, cur.Qsc, pageResult, ct);
            answered++;
        }
        h.LastDialogConcluded = concluded; // let the leveler know the accept/hand-in reached its terminal page
        h.Log($"quest dialogue done (npc h={npcHandle}, {answered} pages acked, concluded={concluded}, rewardSelected={rewardSelected})");
        return ActionResult.Sent;
    }

    /// <summary>Derive the per-page MENU answers for a quest straight from its OWN script (data-driven, no hardcoding)</summary>
    public Dictionary<int, uint> DeriveMenuAnswers(int questId)
    {
        var map = new Dictionary<int, uint>();
        var q = ClientData?.Quest(questId);
        if (q is null) return map;
        foreach (var script in new[] { q.StartScript, q.ActionScript, q.FinishScript })
            ParseScriptMenus(script, map);
        return map;
    }

    private static readonly System.Text.RegularExpressions.Regex ReScriptIf =
        new(@"^IF\s+RESULT\s*==\s*(\d+)\s+GOTO\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ReScriptGoto =
        new(@"^GOTO\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ReScriptSay =
        new(@"^SAY\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void ParseScriptMenus(string? script, Dictionary<int, uint> map)
    {
        if (string.IsNullOrWhiteSpace(script)) return;
        var lines = script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var label = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].StartsWith(':')) label[lines[i][1..].Trim()] = i;

        // Does the block at :lbl reach a TERMINAL (following GOTO + nested IF-RESULT menus)?
        // ACCEPT terminates a start script; DONE terminates a FINISH script. Only ACCEPT counted, so on a
        // hand-in quiz every branch scored false, no answer was derived, and the page defaulted to option 1
        // -- Lost Love 2 (q430) looped SAY 20206 <-> 20207 forever and threw away 1,173 exp on every bot.
        bool BlockReaches(string lbl, HashSet<string> visiting)
        {
            if (!visiting.Add(lbl) || !label.TryGetValue(lbl, out var start)) return false;
            bool reached = false;
            for (int i = start + 1; i < lines.Count; i++)
            {
                var ln = lines[i];
                if (ln.StartsWith(':') || ln.Equals("END", StringComparison.OrdinalIgnoreCase)) break;
                if (ln.Equals("ACCEPT", StringComparison.OrdinalIgnoreCase)
                    || ln.Equals("DONE", StringComparison.OrdinalIgnoreCase)) { reached = true; break; }
                var im = ReScriptIf.Match(ln);
                if (im.Success) { if (BlockReaches(im.Groups[2].Value, visiting)) { reached = true; break; } continue; }
                var gm = ReScriptGoto.Match(ln);
                if (gm.Success) { reached = BlockReaches(gm.Groups[1].Value, visiting); break; }
            }
            visiting.Remove(lbl);
            return reached;
        }

        int lastSayId = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var sm = ReScriptSay.Match(lines[i]);
            if (sm.Success) { lastSayId = int.Parse(sm.Groups[1].Value); continue; }
            if (ReScriptIf.IsMatch(lines[i]) && lastSayId != 0)
            {
                // A WRONG quiz answer loops BACK to the menu (`:MARK1 / SAY 20207 / GOTO MARK0`), so it can still
                // reach the terminal the long way round -- through the very page we are answering. Seeding the
                // visit set with the ENCLOSING label makes any branch that returns here score false, which is what
                // distinguishes the one answer that completes the quest from the four that re-ask the question.
                var enclosing = lines.Take(i).LastOrDefault(l => l.StartsWith(':'))?[1..].Trim();
                uint answer = 0; int j = i;
                for (; j < lines.Count; j++)
                {
                    var rm = ReScriptIf.Match(lines[j]);
                    if (!rm.Success) break;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (enclosing is not null) seen.Add(enclosing);
                    if (BlockReaches(rm.Groups[2].Value, seen))
                    { answer = uint.Parse(rm.Groups[1].Value); break; }
                }
                if (answer != 0) map[lastSayId] = answer;
                lastSayId = 0; i = j - 1;
            }
        }
    }

    /// <summary>Select a quest reward item by index (NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD)</summary>
    public Task<ActionResult> SelectQuestRewardAsync(string id, ushort questId, uint itemIndex, CancellationToken ct = default)
        => ActAsync(id, $"quest {questId} reward index {itemIndex}",
            s => s.SendAsync(new PROTO_NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD { nQuestID = questId, nSelectedItemIndex = itemIndex }, ct));

    /// <summary>Use an inventory item by slot (invenType: 0 = normal bag)</summary>
    public Task<ActionResult> UseItemAsync(string id, byte slot, byte invenType, CancellationToken ct = default)
        => ActAsync(id, $"use item slot={slot} type={invenType}",
            s => s.SendAsync(new PROTO_NC_ITEM_USE_REQ { invenslot = slot, invenType = invenType }, ct));

    /// <summary>Spend ONE unspent stat point on stat (0=STR,1=END,2=DEX,3=INT,4=MP — CHARSTATDISTSTR order)</summary>
    public Task<ActionResult> IncStatAsync(string id, byte stat, CancellationToken ct = default)
        => ActAsync(id, $"inc stat {stat}",
            s => s.SendAsync(new PROTO_NC_CHAR_STAT_INCPOINT_REQ { stat = stat }, ct));

    /// <summary>Equip the inventory item at (the server derives the target equipment slot from the item itself)</summary>
    public Task<ActionResult> EquipAsync(string id, byte slot, CancellationToken ct = default)
        => ActAsync(id, $"equip inventory slot {slot}",
            s => s.SendAsync(new PROTO_NC_ITEM_EQUIP_REQ { slot = slot }, ct));

    /// <summary>Pick up a ground item by its entity handle (NC_ITEM_PICK_REQ {itemhandle}, the handle from / a DROPEDITEM broa…</summary>
    public Task<ActionResult> PickupAsync(string id, ushort itemHandle, CancellationToken ct = default)
        => ActAsync(id, $"pickup item h={itemHandle}", s =>
        {
            // Arm the pick-ack pacing gate (ZoneView.CanPick): the server handles ONE item-cell pick at a time — the driver…
            if (_bots.TryGetValue(id, out var hh)) hh.ZoneView?.MarkPickSent();
            return s.SendAsync(new PROTO_NC_ITEM_PICK_REQ { itemhandle = itemHandle }, ct);
        });

    /// <summary>Fire the client's inventory "auto sort": NC_ITEM_AUTO_ARRANGE_INVEN_REQ (0x304A, empty payload)</summary>
    public Task<ActionResult> SortInventoryAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "inventory auto-sort (0x304A)", s =>
            s.SendAsync(new PROTO_NC_ITEM_AUTO_ARRANGE_INVEN_REQ(), ct));

    /// <summary>Drop (or destroy) a bag item onto the ground: NC_ITEM_DROP_REQ (0x3007) {slot ITEM_INVEN (box&amp;lt;&amp;lt;10|slot),…</summary>
    public Task<ActionResult> DropItemAsync(string id, int slot, uint lot = 1, int box = 9, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var h) || h.Position is not { } pos)
            return Task.FromResult(ActionResult.NotInZone);
        var req = new PROTO_NC_ITEM_DROP_REQ
        {
            slot = new ITEM_INVEN { Inven = (ushort)(((box & 0x3F) << 10) | (slot & 0x3FF)) },
            lot = lot,
            loc = new SHINE_XY_TYPE { x = (uint)pos.X, y = (uint)pos.Y },
        };
        return ActAsync(id, $"drop item box={box} slot={slot} lot={lot} (0x3007)", s => s.SendAsync(req, ct));
    }

    /// <summary>Loot a ground drop: walk to it (pathfinding over the current map's grid), then pick it up and wait for it to l…</summary>
    public async Task<ActionResult> LootAsync(string id, ushort itemHandle = 0, double unitsPerSec = 120.0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        if (handle.ZoneView is not { } view || handle.Position is not { } pos) return ActionResult.NotInZone;

        var drop = itemHandle != 0
            ? view.Drops.FirstOrDefault(d => d.Handle == itemHandle)
            : view.NearestDrop(pos.X, pos.Y);
        if (drop is null) return ActionResult.NotFound;

        // Walk onto the item, then pick
        await ApproachAsync(id, handle, drop.X, drop.Y, stopShort: 0, unitsPerSec, ct);
        view.MarkPickSent(); // same pick-ack pacing gate as PickupAsync
        await s.SendAsync(new PROTO_NC_ITEM_PICK_REQ { itemhandle = drop.Handle }, ct);
        handle.Log(BotLogLevel.Verbose, $"loot item {drop.ItemId} (h={drop.Handle}) @({drop.X},{drop.Y})");

        // Success = the drop left view (picked/despawned)
        var picked = await WaitUntilAsync(() => view.Drops.All(d => d.Handle != drop.Handle), 3000, ct);
        handle.Log(BotLogLevel.Info, picked ? $"looted h={drop.Handle}" : $"loot h={drop.Handle} unconfirmed — still on ground, reason NOT established");
        return ActionResult.Sent;
    }

    // Move/run (ACT MoverunCmd, 0x2019): 16 bytes = fromX,fromY,toX,toY (u32 LE)
    private static readonly ushort OpMoveRun =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.MoverunCmd);

    /// <summary>How far to step when a cast is refused as out-of-range from inside our own melee stop radius. Big
    /// enough that the server must answer it (accept or MOVEFAIL), small enough not to abandon the fight.</summary>
    private const double DesyncProbeStep = 40.0;

    /// <summary>Max distance (world units) of a single MoverunCmd</summary>
    private const double MaxMoveStep = 250.0;

    /// <summary>Walk a precomputed path: stream MoverunCmd steps on a background task, paced to the bot's current (updated liv…</summary>
    public ActionResult WalkPath(string id, IReadOnlyList<(uint X, uint Y)> waypoints, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session) return ActionResult.NotInZone;
        if (waypoints.Count < 2) return ActionResult.Sent;
        // Per-walk cancellation (linked to the bot's lifetime) so a MOVEFAIL can abort just this walk
        var walkCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.WalkCts?.Cancel();
        handle.WalkCts = walkCts;
        handle.WalkPlan = waypoints;   // so the watch UI can draw where we are trying to GO, not just where we are
        handle.WalkPlanIndex = 1;      // heading toward waypoint 1; waypoint 0 is where we started
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
                    handle.WalkPlanIndex = i + 1;   // the waypoint we are walking toward right now
                    var segDist = Math.Sqrt(Math.Pow((double)tx - fx, 2) + Math.Pow((double)ty - fy, 2));
                    var subSteps = Math.Max(1, (int)Math.Ceiling(segDist / MaxStepFor(unitsPerSec)));
                    double cx = fx, cy = fy;
                    for (int k = 1; k <= subSteps && !ct.IsCancellationRequested; k++)
                    {
                        // Interpolate the next intermediate point along the segment
                        var sx = (uint)Math.Round(fx + (tx - (double)fx) * k / subSteps);
                        var sy = (uint)Math.Round(fy + (ty - (double)fy) * k / subSteps);
                        // HOLD STILL WHILE A CAST BAR IS OPEN — moving cancels the cast
                        while (handle.ZoneView is { CastBarActive: true } && !ct.IsCancellationRequested)
                            await Task.Delay(100, ct);
                        // AND WHILE ROOTED/STUNNED. This is not politeness, it is the position model: BeginMove
                        // below advances our BELIEVED position as each step is SENT, so streaming steps at a server
                        // that is refusing to move us walks the belief away from the truth with nothing to correct
                        // it. Measured 2026-08-19: a root at 16:44:36 (moveBlock=True) landed mid-run, and 50s later
                        // the bot was casting at a target it believed was 35u away while the server rejected every
                        // cast as OUT OF RANGE — because it was measuring from where we actually were.
                        while (handle.ZoneView is { Rooted: true } && !ct.IsCancellationRequested)
                            await Task.Delay(100, ct);
                        var paceSpeed = handle.WalkSpeed > 0 ? handle.WalkSpeed : unitsPerSec;
                        var from = handle.BeginMove(sx, sy, paceSpeed);
                        var p = new byte[16];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), from.X);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), from.Y);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), sx);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), sy);
                        await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                        handle.LastMoveTarget = (sx, sy);  // the tile we're trying to enter (for MOVEFAIL learning)
                        var stepDist = Math.Sqrt(Math.Pow(sx - (double)from.X, 2) + Math.Pow(sy - (double)from.Y, 2));
                        steps++;
                        await Task.Delay((int)Math.Clamp(stepDist / paceSpeed * 1000, 40, 2000), ct);
                        cx = sx; cy = sy;
                    }
                }
                handle.Log(BotLogLevel.Verbose, $"walk-path done ({waypoints.Count} waypoints, {steps} move steps)");
            }
            catch (OperationCanceledException) { handle.Log("walk-path aborted (cancelled / move blocked)"); }
            catch (Exception ex) { handle.Log($"walk-path error: {ex.Message}"); }
            finally
            {
                // Only clear the plan if THIS walk still owns it — a newer WalkPath may already have replaced both.
                if (ReferenceEquals(handle.WalkCts, walkCts)) { handle.WalkCts = null; handle.WalkPlan = null; }
                walkCts.Dispose();
            }
        }, ct);
        return ActionResult.Sent;
    }

    public async Task<ActionResult> WalkAsync(string id, uint fromX, uint fromY, uint toX, uint toY, CancellationToken ct = default)
    {
        if (_bots.TryGetValue(id, out var casting) && casting.ZoneView is { CastBarActive: true } zvCast)
        {
            // Throttled: a suppressed walk is a decision worth seeing, but the nav retries fast
            if (DateTime.UtcNow - casting.LastCastBarWalkLogUtc > TimeSpan.FromMilliseconds(900))
            {
                casting.LastCastBarWalkLogUtc = DateTime.UtcNow;
                casting.Log($"[nav] walk SUPPRESSED — cast bar open {(DateTime.UtcNow - zvCast.CastBarStartedAtUtc).TotalMilliseconds:F0}ms " +
                            "(moving would cancel the cast); holding still until it resolves");
            }
            return ActionResult.Sent;   // treat as handled: the caller must not escalate/re-path on this
        }
        // THE CALLER'S `from` IS NOT TRUSTED (2026-08-13)
        var live = _bots.TryGetValue(id, out var mh)
            ? mh.BeginMove(toX, toY, mh.WalkSpeed > 0 ? mh.WalkSpeed : 120.0)
            : (X: fromX, Y: fromY);
        var drift = Math.Sqrt(Math.Pow(live.X - (double)fromX, 2) + Math.Pow(live.Y - (double)fromY, 2));
        var label = drift > 8
            ? $"walk ({live.X},{live.Y})->({toX},{toY}) [caller said from ({fromX},{fromY}), drift {drift:F0}u]"
            : $"walk ({live.X},{live.Y})->({toX},{toY})";
        return await ActAsync(id, label, s =>
        {
            var p = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), live.X);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), live.Y);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), toX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), toY);
            return s.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
        });
    }

    public Task<ActionResult> GmAsync(string id, string command, CancellationToken ct = default)
        => ActAsync(id, $"gm: {command}", s => s.SendAsync(ChatCodec.BuildChatReq(command), ct));

    /// <summary>Shared plumbing for a manual action on an in-zone bot: resolve → guard phase → send → log</summary>
    private async Task<ActionResult> ActAsync(string id, string logLine, Func<BotSession, Task> send)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session)
            return ActionResult.NotInZone;
        await send(session);
        handle.Log(logLine);
        return ActionResult.Sent;
    }

    /// <summary>Like but sends on the bot's WM link (party / friend traffic is WorldManager-side)</summary>
    private async Task<ActionResult> WmActAsync(string id, string logLine, Func<BotSession, Task> send)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.WmSession is not { } wm) return ActionResult.NotInZone;
        await send(wm);
        handle.Log(logLine);
        return ActionResult.Sent;
    }

    /// <summary>WM action variant that also passes the bot's own character name (the charid Name5 the friend structs require)</summary>
    private Task<ActionResult> WmActAsync(string id, string logLine, Func<BotSession, string, Task> send)
        => WmActAsync(id, logLine, s =>
        {
            var self = _bots.TryGetValue(id, out var h) ? h.CharName ?? "" : "";
            return send(s, self);
        });

    /// <summary>Max single-MoverunCmd distance for a given walk speed</summary>
    private static double MaxStepFor(double unitsPerSec) => Math.Max(MaxMoveStep, unitsPerSec * (MaxMoveStep / 120.0));

    /// <summary>React to a gate / town-portal transition: advance the tracked position to the new spawn coord and update the c…</summary>
    private void OnMapChanged(BotHandle handle, MapHandoff h, Action<string> log)
    {
        handle.SetPosition(h.X, h.Y);
        // Raise this BEFORE bumping the sequence, so a waiter woken by the bump can never observe the transition
        // without also observing that the link behind it is about to go away.
        if (h.IsCrossServer) handle.HandoffInFlight = true;
        handle.BumpMapChange(); // wake any travel loop waiting on a transition
        // GROUND TRUTH FIRST. The wire carries a map ID; MapInfo.shn is the client's own ID -> MapName table and it
        // covers every id the server sends (138 rows, verified 2026-08-19 against the ids seen live: 6/9/10/45/75/150).
        // This order used to be inverted -- a learned CACHE, then our own travel INTENT, and only then the table --
        // which let a guess outrank the answer. That is the whole shape of the nine-hour wedge: the intent branch also
        // called Catalog.Learn(), baking the wrong id->name pair in for the life of the process, after which every
        // later arrival at that id resolved to the wrong name via "catalog" and pathfinding loaded the wrong .shbd.
        // Nothing short of a relog could clear it. An intent can never be more authoritative than the table.
        var name = ClientData?.MapName(h.MapId);
        var nameSource = name is null ? null : "MapInfo.shn";
        // The learned cache is now only for ids MapInfo.shn does NOT know.
        if (name is null && Catalog.NameFor(h.MapId) is { } cached)
        {
            name = cached;
            nameSource = "catalog";
        }
        // LAST RESORT: where we INTENDED to go, which is not the same as where the server put us. Only reachable for an
        // id no client table knows, and it is still learned so the next arrival is at least consistent -- but it can no
        // longer overwrite a name the client data could have supplied.
        if (name is null && handle.PendingDestMap is { } pending)
        {
            name = pending;
            nameSource = "PENDING-INTENT";
            Catalog.Learn(h.MapId, pending);
        }
        // Keep the cache in step with the table so IdFor() reverse lookups still work.
        if (nameSource == "MapInfo.shn" && name is { } clientName) Catalog.Learn(h.MapId, clientName);
        // INVARIANT GUARD, not a diagnostic: after the reordering above this can only fire if a name came from the cache
        // or the intent for an id MapInfo.shn also knows -- which the order now makes impossible. It stays because it is
        // the assertion that the order is still right, and it is free.
        var truth = ClientData?.MapName(h.MapId);
        if (truth is not null && name is not null && !string.Equals(truth, name, StringComparison.OrdinalIgnoreCase))
            handle.Log(BotLogLevel.Note,
                $"⛔ MAP IDENTITY MISMATCH: naming ourselves '{name}' (via {nameSource}) but MapInfo.shn says " +
                $"mapId={h.MapId} is '{truth}'. Pathfinding will use the WRONG .shbd — expect a MOVEFAIL storm.");
        handle.SetCurrentMap(name ?? $"map#{h.MapId}", h.MapId, nameSource ?? "unresolved");
        log($"[nav] now on {name} (mapId={h.MapId}, via {nameSource ?? "unresolved"}) at ({h.X},{h.Y})" +
            (h.IsCrossServer ? $" — cross-server handoff to {h.Ip}:{h.Port}, reconnecting" : " (in-band)"));

        // FIELD .sbi door learning is PER-VISIT — reset it on every map entry (operator 2026-07-22): the Eld "Puzzle God…
        if (name is { } nm && GridProvider?.Invoke(nm) is { HasDoors: true } doorGrid) doorGrid.ResetDoorLearning();

        // In-band LINKSAME: re-send MAP_LOGINCOMPLETE so the server spawns us into the new map and starts broadcasting i…
        if (!h.IsCrossServer && handle.ZoneSession is { } zs)
        {
            _ = zs.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
            log($"[nav] >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) to spawn into {name}");
        }
    }

    private const int CrossServerHandoffSettleMs = 600;

    private const int BashWindupMs = 450;
    private const int WatchdogPollMs = 5_000;
    private const int WatchdogStillSecs = 45;

    private async Task RunBotAsync(BotHandle handle)
    {
        var opt = handle.Options;
        var ct = handle.Cts.Token;
        void Log(string m) { handle.Log(m); _globalLog?.Invoke($"[{handle.Id}] {m}"); }

        FiestaClientConnectionScope wm = default;
        try
        {
            var chain = new LoginChain(_xorTable, Log);

            // If requested at spawn, start the packet dump NOW — before the first connect — so the login handshake AND the z…
            if (opt.Announce) handle.AnnounceChat = true;   // restored from the roster, so it survives a rollout
            if (opt.PacketLog && handle.PacketLog is null)
            {
                var dir = Environment.GetEnvironmentVariable("PACKETLOG_DIR") ?? Directory.GetCurrentDirectory();
                handle.PacketLog = new Net.PacketLog(System.IO.Path.Combine(dir, $"packets-{handle.Id}.log"));
                Log($"packet log ENABLED (from spawn) -> {handle.PacketLog.Path}");
            }
            // ALWAYS tap: the always-on PacketRing needs every frame, and CombinedTap adds the file log only when one is ena…
            Action<bool, ushort, ReadOnlyMemory<byte>>? tap = handle.CombinedTap;

            handle.SetPhase(BotPhase.LoggingIn);
            var login = await chain.RunLoginAsync(
                new FiestaEndpoint(opt.Host, opt.LoginPort), opt.Credentials, opt.WorldNo, ct, tap);
            var wmPort = login.WmAdvertised.Port == 0 ? opt.WmPortFallback : login.WmAdvertised.Port;
            var wmEp = new FiestaEndpoint(opt.Host, wmPort);

            handle.SetPhase(BotPhase.SelectingChar);
            // Fill in a VALID appearance before creating a character
            var createSpec = opt.CreateSpec;
            var (wmResult, wmConn) = await chain.RunWmAsync(
                wmEp, opt.Credentials, login.Otp, opt.Slot, createSpec, ct, tap, opt.Character);
            wm = new FiestaClientConnectionScope(wmConn);

            if (wmResult.ZoneAdvertised is not { } zoneAdv || wmResult.Selected is not { } sel)
                throw new InvalidOperationException(
                    "account has no character to enter a zone (and no create spec)");
            handle.SetCharName(sel.Name);
            handle.SetLevel(sel.Level); // authoritative level from the WM avatar list (not inferred)
            handle.SetClass(sel.Class); // ClassID for class-appropriate quest-reward selection

            var zoneEntry = ZoneEntry.FromDataDir(_xorTable, Log, opt.DataDir);

            // The WM link stays open for the bot's whole in-zone life and across any cross-server handoffs (each zone valida…
            var wmSession = new BotSession(wmConn, sel.Name, wmResult.WmHandle, wmEp, Log,
                linkTag: "wm", logInbound: opt.LogInbound);
            handle.WmSession = wmSession;
            // Same connection diagnostics on the WM link — a half-open WM socket is exactly the ghost-session shape, and it…
            wmSession.ConnDiag = m => handle.Log(BotLogLevel.Note, m);
            if (handle.PacketLog is { } plw) wmSession.PacketTap = plw.Tap; // re-attach packet log if enabled
            TrackPartyInvites(handle, wmSession); // capture incoming party invites (the inviter)
            // The WM read loop gets its OWN linked CTS so an UNEXPECTED zone-only drop (server kick / net blip, where the bo…
            using var wmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wmRun = wmSession.RunAsync(wmCts.Token);

            var zoneEp = new FiestaEndpoint(opt.Host, zoneAdv.Port);
            var zoneWmHandle = wmResult.WmHandle;
            // The zone login ack has no map name, but the WM avatar list does (PROTO_AVATARINFORMATION.loginmap) — that's th…
            var currentMap = string.IsNullOrWhiteSpace(sel.LoginMap) ? opt.StartMap : sel.LoginMap;
            var firstEntry = true;
            int burstRetries = 0; const int MaxBurstRetries = 3;
            while (true)
            {
                // THE WM LINK IS CREATED ONCE, ABOVE THIS LOOP — SO A DEAD WM POISONS EVERY ITERATION
                if (wmRun.IsCompleted && !ct.IsCancellationRequested)
                {
                    Log($"WM link is already dead ({wmSession.State.DisconnectReason ?? "completed"}) — a zone " +
                        $"session cannot be validated without it, so NOT reconnecting the zone in place. " +
                        $"Falling through to a full re-login.");
                    break;
                }

                handle.SetPhase(BotPhase.EnteringZone);
                handle.ZoneSession = null; // no live zone link during (re)connect
                var entry = await zoneEntry.EnterAsync(zoneEp, zoneWmHandle, sel.Name, ct, tap);
                // BURST login (no explicit [1802]): position/HP were NOT seeded → nav broken (can't find gates) → the freeze/sto…
                if (entry.WasBurst && burstRetries < MaxBurstRetries)
                {
                    burstRetries++;
                    Log($"[nav] BURST login (no [1802], char-info unseeded) — retry {burstRetries}/{MaxBurstRetries} for a clean login");
                    entry.Conn.Dispose();
                    await Task.Delay(1500, ct);
                    continue;   // re-enter the zone for a proper MAP_LOGIN_ACK
                }
                if (!entry.WasBurst) burstRetries = 0;
                var zoneConn = entry.Conn;
                if (entry.SpawnX is { } spx && entry.SpawnY is { } spy) handle.SetPosition(spx, spy);
                if (entry.CharHandle is { } selfH) handle.SetSelfHandle(selfH);

                // Tripped to break THIS zone session when a cross-server handoff lands, without disturbing the WM loop or the bo…
                using var zoneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                MapHandoff? handoff = null;

                await using var zoneSession = new BotSession(zoneConn, sel.Name, zoneWmHandle, zoneEp, Log,
                    linkTag: "zone", logInbound: opt.LogInbound);
                handle.ZoneSession = zoneSession;
                // Surface connection-level anomalies (send-after-dispose, send-lock contention/timeout, half-open write timeout)…
                zoneSession.ConnDiag = m => handle.Log(BotLogLevel.Note, m);
                if (handle.PacketLog is { } plz) zoneSession.PacketTap = plz.Tap; // re-attach packet log across handoff
                // Party MEMBER-STATE also flows ZONE-side for CO-LOCATED members: live HP (MEMBERINFORM 50) and positions (MEMBE…
                zoneSession.PacketReceived += pkt =>
                {
                    try
                    {
                        if (pkt.Opcode == OpPartyMemberList) ParsePartyRoster(handle, pkt.Payload.Span);
                        else if (IsPartyMemberStateOpcode(pkt.Opcode)) HandlePartyMemberState(handle, pkt.Opcode, pkt.Payload.Span);
                    }
                    catch { /* ignore an unparseable zone party frame */ }
                };

                // Perception model (nearby players + chat) is always on — cheap, and the status/say surface and any behavior rea…
                using var zoneView = new ZoneView(zoneSession, Log, handle.Log);
                zoneView.QuestNameResolver = qid => ClientData?.QuestName(qid) ?? $"q{qid}";  // log quest NAMES, not bare ids
                // Lets the 0x4804 learn-confirmation tell an ACTIVE learn from a PASSIVE one (overlapping id spaces) by looking…
                zoneView.ScrollSkillResolver = itemId => ClientData?.ScrollSkill(itemId) ?? (-1, false);
                handle.ZoneView = zoneView;
                // DYNAMIC SCENARIO-DOOR COLLISION (2026-07-15): push live door states into the map's pathfinding grid so closed…
                zoneView.DoorStatesByNameChanged += states =>
                {
                    if (handle.CurrentMap is { } dmap && GridProvider?.Invoke(dmap) is { HasDoors: true } dgrid)
                        dgrid.SetDoorStates(states);
                };
                zoneView.EntityChanged += _ =>
                {
                    if (handle.CurrentMap is not { } pmap || GridProvider?.Invoke(pmap) is not { HasDoors: true } pgrid) return;
                    var boards = zoneView.NearbyNpcs
                        .Where(n => BlockGrid.IsPuzzlePieceMob(n.MobId))
                        .Select(n => (n.X, n.Y, (int)n.MobId));
                    foreach (var name in pgrid.NotePuzzleEntities(boards))
                        Log($"[nav] ⛔ .sbi door '{name}' is CLOSED — puzzle board (mob {BlockGrid.PuzzleMobBoard}) is inside its box, so a game is running. Walled the courtyard from the ENTITY signal (no MOVEFAIL probing needed) and re-pathing around it.");
                };
                if (entry.CharHandle is { } selfH2)
                {
                    zoneView.SelfHandle = selfH2; // for MOVESPEED filtering
                    // Stamp it into the packet log too — see PacketLog.NoteSelfHandle for why analysis must never infer this
                    handle.PacketLog?.NoteSelfHandle(selfH2, handle.CharName ?? handle.Options.Character);
                }
                zoneView.SelfPositionProvider = () => handle.Position; // for aggro (mob running at us)
                RegisterMetrics(handle, zoneView);
                // DURABLE THREAT TABLE: seed what we already know about how hard each mob hits, and push every new sample back o…
                zoneView.SeedMobHits(Knowledge.MobThreatsFor(handle.KnowledgeScope)
                    .Select(kv => (kv.Key, kv.Value.Max, kv.Value.Count, kv.Value.Sum)));
                zoneView.MobHitSampled = (mobId, dmg) => Knowledge.RecordMobHit(handle.KnowledgeScope, mobId, dmg);
                zoneView.ScalarLearned = (name, val) => Knowledge.RecordScalar(handle.KnowledgeScope, name, val);
                var cdStat = Knowledge.Scalar(handle.KnowledgeScope, ZoneView.ScalarStoneCooldownMs);
                var healStat = Knowledge.Scalar(handle.KnowledgeScope, ZoneView.ScalarStoneHeal);
                zoneView.SeedScalars(
                    cdStat?.Min ?? -1,                                            // MIN: converges from above
                    healStat is { Count: > 0 } ? healStat.Sum / healStat.Count : -1,
                    healStat?.Count ?? 0,
                    healStat?.Max ?? -1);
                var mrStat = Knowledge.Scalar(handle.KnowledgeScope, ZoneView.ScalarMeleeRange);
                var mrSeed = mrStat is { Count: > 0 } ms ? ms.Sum / ms.Count : -1;
                zoneView.SeedMeleeRange(mrSeed);
                zoneView.IsInsideScenarioArea = (areaName, pos) =>          // hold AREAENTRY_ACK until inside the .aid box
                {
                    if (handle.CurrentMap is not { } m || AreaProvider?.Invoke(m) is not { } areas) return true; // no data → ack now
                    var a = areas.FirstOrDefault(ar => string.Equals(ar.Name, areaName, StringComparison.OrdinalIgnoreCase));
                    if (a is null) return true;
                    return Math.Abs(pos.X - a.CenterX) <= a.HalfX && Math.Abs(pos.Y - a.CenterY) <= a.HalfY;
                };
                if (ClientData is { } cdata) zoneView.IsHuntableMob = mobId => cdata.IsHuntableEnemy(mobId); // ignore guards
                if (ClientData is { } cdata2)
                {
                    zoneView.IsMoveBlockingAbstate = idx => cdata2.IsMoveBlockingAbstate(idx); // root/stun → don't learn walls
                    zoneView.IsStunAbstate = idx => cdata2.IsStunAbstate(idx);                 // stun vs root, for the metrics
                }
                zoneView.SeedMaxVitals(entry.MaxHp, entry.MaxSp);
                zoneView.SeedMaxStones(entry.MaxHpStone, entry.MaxSpStone); // reserve capacity from [1802]
                zoneView.SeedStones(entry.CurHpStone, entry.CurSpStone); // real reserve from zone-enter char-info
                if (entry.Cen is { } cen0) zoneView.SeedMoney((long)cen0); // money from char-info — never leave it -1
                if (entry.Exp is { } exp0) zoneView.SeedExp((long)exp0); // exp from char-info — track grind progress live
                // Level from char-info (NC_CHAR_BASE Level@25) — authoritative on EVERY zone-enter, so bot.level() advances with…
                if (entry.Level is { } lvl0 && lvl0 > 0) handle.SetLevel(lvl0);
                zoneView.SeedSkills(entry.Skills);
                zoneView.SeedPassives(entry.Passives);
                zoneView.SeedStats(entry.Stats);   // STR/END/.../DEF/M.Def for the watch panel
                zoneView.SeedItems(entry.Items);
                zoneView.SeedQuests(entry.DoneQuests, entry.ActiveQuests, entry.ReadQuests);
                // MAP IDENTITY AT ZONE-ENTER. The name here comes from the WM avatar list (loginmap) because
                // MAP_LOGIN_ACK carries no map at all -- PROTO_NC_CHAR_MAPLOGIN_ACK is {charhandle, CHAR_PARAMETER_DATA,
                // logincoord} and CHAR_PARAMETER_DATA has no map field (PDB extract, checked 2026-08-19). So resolve the
                // id from the name via MapInfo.shn rather than leaving CurrentMapId null for the whole session: without
                // it, "which map does the bot think it is on, and does the server agree" is unanswerable exactly when
                // the bot is wedged and has had no recent transition to re-derive it.
                handle.SetCurrentMap(currentMap, ClientData?.MapId(currentMap), "WM-loginmap");
                zoneView.MapChanged += h =>
                {
                    // A map change ends combat server-side: battle mode and any running swing stream are gone, so re-assert them on…
                    handle.InBattleMode = false;
                    handle.BashTarget = 0;
                    if (handle.ZoneView is { } zvm) zvm.BashActive = false;
                    OnMapChanged(handle, h, Log);
                    // EMIT AFTER OnMapChanged, NOT BEFORE. Subscribers react by re-reading handle state -- the /stream
                    // pump answers this event by resending its FULL state, which reads bot.CurrentMap. Emitting first
                    // meant that resend captured the OLD map name and overwrote the correct one the event itself had
                    // just delivered, so the watch UI drew the previous map's art and filtered every NPC out of the
                    // frame (its npc list is gated on lastEnt.map === drawnMap). The event is the same either way; only
                    // the order of "tell them" and "be ready to be asked" changes.
                    handle.Emit(new BotEvent(BotEventKind.MapChanged, h));
                    if (h.IsCrossServer) { handoff = h; zoneCts.Cancel(); } // break to reconnect
                };
                zoneView.MoveFailed += pos =>
                {
                    // Server rejected a move into an off-grid obstacle: resync to its truth and abort the current walk so we stop pu…
                    handle.SetPosition(pos.X, pos.Y);
                    handle.WalkCts?.Cancel();
                    // DON'T LEARN A WALL WHILE ROOTED: if a movement-blocking abstate (stun/root/entangle) is active, the server MOV…
                    if (zoneView.Rooted)
                    {
                        Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) while ROOTED (stun/entangle) — NOT learning a wall, waiting out the state");
                        handle.Emit(new BotEvent(BotEventKind.MoveFailed, pos));
                        return;
                    }
                    // DON'T LEARN A WALL INSIDE A SCENARIO INSTANCE either: the block is almost always a dynamic scenario DOOR (KQ_G…
                    if (zoneView.InScenarioInstance)
                    {
                        // Learn the rejected cell with a SHORT TTL (not permanent)
                        bool learnedTtl = false;
                        if (handle.LastMoveTarget is { } tgtI && handle.CurrentMap is { } mapI &&
                            GridProvider?.Invoke(mapI) is { } gridI)
                        {
                            double dxI = (double)tgtI.X - pos.X, dyI = (double)tgtI.Y - pos.Y;
                            double lenI = Math.Sqrt(dxI * dxI + dyI * dyI);
                            if (lenI > 1)
                            {
                                double aheadI = BlockGrid.WorldPerTile * 1.5;
                                var axI = (uint)Math.Max(0, pos.X + dxI / lenI * aheadI);
                                var ayI = (uint)Math.Max(0, pos.Y + dyI / lenI * aheadI);
                                var (ttxI, ttyI) = gridI.WorldToTile(axI, ayI);
                                gridI.MarkBlockedTtl(ttxI, ttyI, 5000);
                                learnedTtl = true;
                                Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) in a SCENARIO INSTANCE → TTL-blocked cell ({ttxI},{ttyI}) 5s (route around; auto-clears for reopening doors; total {gridI.RuntimeBlockedCount})");
                            }
                        }
                        if (!learnedTtl) Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) in a SCENARIO INSTANCE — no move target, resynced");
                        // PERPENDICULAR-TO-WALL UNSTICK (operator 2026-07-13)
                        bool near = handle.LastMoveFailPos is { } lp &&
                                    Math.Abs((double)lp.X - pos.X) < 60 && Math.Abs((double)lp.Y - pos.Y) < 60;
                        handle.MoveFailStreak = near ? handle.MoveFailStreak + 1 : 1;
                        handle.LastMoveFailPos = pos;
                        if (handle.MoveFailStreak >= 3 && (DateTime.UtcNow - handle.LastUnstickUtc).TotalMilliseconds > 1500
                            && handle.CurrentMap is { } umap && GridProvider?.Invoke(umap) is { } ugrid
                            && ugrid.NearestBlockedDir(pos.X, pos.Y) is { } wall)
                        {
                            handle.LastUnstickUtc = DateTime.UtcNow;
                            const double UNSTICK = 200.0;
                            // The two directions ALONG the wall (± perpendicular to the wall normal)
                            var opts = new (double dx, double dy)[] { (-wall.dy, wall.dx), (wall.dy, -wall.dx) };
                            foreach (var o in opts)
                            {
                                var ux = (uint)Math.Max(0, pos.X + o.dx * UNSTICK);
                                var uy = (uint)Math.Max(0, pos.Y + o.dy * UNSTICK);
                                if (!ugrid.IsWalkableWorld(ux, uy)) continue;
                                handle.WalkCts?.Cancel(); // stop the wall-grinding walk
                                _ = WalkAsync(handle.Id, pos.X, pos.Y, ux, uy);
                                Log($"[nav] UNSTICK: wedged x{handle.MoveFailStreak} @({pos.X},{pos.Y}) — sidestep ⊥wall to ({ux},{uy})");
                                handle.MoveFailStreak = 0;
                                break;
                            }
                        }
                        // LAST-RESORT WEDGE RECOVERY (2026-07-16): if the ⊥-unstick can't free the bot (boxed in / no walkable sidestep)…
                        if (handle.MoveFailStreak >= 8 && handle.CurrentMap is { } wmap
                            && GridProvider?.Invoke(wmap) is { } wgrid && wgrid.RuntimeBlockedCount > 0)
                        {
                            var poisoned = wgrid.RuntimeBlockedCount;
                            wgrid.ClearRuntimeBlocked();
                            handle.MoveFailStreak = 0;
                            Log($"[nav] WEDGE RECOVERY: MOVEFAIL-wedged x8+ @({pos.X},{pos.Y}) — cleared {poisoned} poisoned runtime-blocks, re-pathing on the clean .shbd");
                        }
                        handle.Emit(new BotEvent(BotEventKind.MoveFailed, pos));
                        return;
                    }
                    // MEASURING STICK (2026-07-21): on a FIELD MOVEFAIL, compare our .shbd walkability against the reverse-engineere…
                    if (handle.LastMoveTarget is { } mt && handle.CurrentMap is { } cmap &&
                        GridProvider?.Invoke(cmap) is { } cg && cg.HasBdt)
                    {
                        double mdx = (double)mt.X - pos.X, mdy = (double)mt.Y - pos.Y;
                        double mlen = Math.Sqrt(mdx * mdx + mdy * mdy);
                        if (mlen > 1)
                        {
                            var sb = new System.Text.StringBuilder();
                            // Sample the rejected ray (and ~2 tiles PAST the target) every 3 units — fine enough to catch a single-tile (6.2…
                            char ps = '?', pb = '?';
                            double end = mlen + BlockGrid.WorldPerTile * 2;
                            for (double t = 0; t <= end + 0.1; t += 3.0)
                            {
                                var sx = (uint)Math.Max(0, pos.X + mdx / mlen * t);
                                var sy = (uint)Math.Max(0, pos.Y + mdy / mlen * t);
                                char s = cg.IsStaticWalkableWorld(sx, sy) ? 'S' : 's';
                                char bd = cg.BdtWalkableWorld(sx, sy) is bool bw ? (bw ? 'B' : 'b') : '-';
                                if (s != ps || bd != pb || t == 0 || t > end - 3.0)
                                    sb.Append($" {t:F0}:{s}{bd}");
                                ps = s; pb = bd;
                            }
                            Log($"[measure] MOVEFAIL pos=({pos.X},{pos.Y}) tgt=({mt.X},{mt.Y}) len={mlen:F0} ray{sb}");
                        }
                    }
                    // Learn the obstacle: block the tile ~1.5 tiles AHEAD of the snap-back position in the DIRECTION we were trying to move — that's the cell the server refused
                    bool handled = false;
                    if (handle.LastMoveTarget is { } tgt && handle.CurrentMap is { } map &&
                        GridProvider?.Invoke(map) is { } grid)
                    {
                        double dx = (double)tgt.X - pos.X, dy = (double)tgt.Y - pos.Y;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len > 1)
                        {
                            double ahead = BlockGrid.WorldPerTile * 1.5;
                            var ax = (uint)Math.Max(0, pos.X + dx / len * ahead);
                            var ay = (uint)Math.Max(0, pos.Y + dy / len * ahead);
                            var (ttx, tty) = grid.WorldToTile(ax, ay);
                            handled = true;
                            // FIELD .sbi DOOR (Eld "Puzzle God"): its closed-state is never on the wire, so learn it from MOVEFAILs inside t…
                            var sbi = grid.NoteMoveFailInSbiDoor(pos.X, pos.Y, tgt.X, tgt.Y);
                            if (sbi == BlockGrid.SbiMoveFail.DoorClosed)
                            {
                                Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) inside a field .sbi door → >{BlockGrid.SbiClosedThreshold} distinct tiles failed = door CLOSED (Eld 'Puzzle God'): walled the whole courtyard, re-pathing AROUND it");
                            }
                            else if (sbi == BlockGrid.SbiMoveFail.Poisoned)
                            {
                                Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) inside a field .sbi door box → poisoned tile ({ttx},{tty}); probing whether the door is closed (re-path, will wall the whole door at >{BlockGrid.SbiClosedThreshold})");
                            }
                            else if (!grid.IsStaticWalkableWorld(ax, ay))
                            {
                                grid.MarkBlocked(ttx, tty);
                                Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) → LEARNED blocked tile ({ttx},{tty}) ahead (real .shbd obstacle), re-routing (total {grid.RuntimeBlockedCount})");
                            }
                            else
                            {
                                Log($"[nav] MOVEFAIL @({pos.X},{pos.Y}) on OPEN GROUND (ahead ({ttx},{tty}) is .shbd-walkable) — transient state (stun/attack-lock/desync), NOT poisoning; resync+re-path (rooted={zoneView.Rooted})");
                            }
                        }
                    }
                    if (!handled) Log($"[nav] move blocked — resynced to ({pos.X},{pos.Y}), walk aborted");
                    handle.Emit(new BotEvent(BotEventKind.MoveFailed, pos));
                };
                zoneView.WalkSpeedChanged += speed => { handle.WalkSpeed = speed; };
                // Forward the perception events onto the stable per-bot hub so a looping script keeps its subscriptions across a…
                zoneView.ChatReceived += msg => handle.Emit(new BotEvent(BotEventKind.Chat, msg));
                zoneView.PlayerAppeared += p => handle.Emit(new BotEvent(BotEventKind.PlayerAppeared, p));
                zoneView.PlayerLeft += h => handle.Emit(new BotEvent(BotEventKind.PlayerLeft, h));
                zoneView.LevelChanged += lvl => { handle.SetLevel(lvl); handle.Log($"level up -> {lvl}"); };
                zoneView.Promoted += cls => { var old = handle.Class; handle.SetClass(cls); handle.Log($"JOB CHANGE: class {old} -> {cls} (PROMOTE_ACK)"); };
                zoneView.HpChanged += hp => handle.Emit(new BotEvent(BotEventKind.Hp, hp));
                zoneView.ExpChanged += exp => handle.Emit(new BotEvent(BotEventKind.Exp, exp));
                zoneView.StonesChanged += () => handle.Emit(new BotEvent(BotEventKind.Stones, null));
                zoneView.MoneyChanged += cen => handle.Emit(new BotEvent(BotEventKind.Money, cen));
                zoneView.LevelChanged += lv => handle.Emit(new BotEvent(BotEventKind.LevelUp, lv));
                zoneView.SkillLearned += (id, lvl, passive) =>
                    handle.Emit(new BotEvent(BotEventKind.SkillLearned, new SkillLearnedInfo(id, lvl, passive)));
                zoneView.SkillCastStarted += (id, target) =>
                    handle.Emit(new BotEvent(BotEventKind.SkillCast, new SkillCastInfo(id, target)));
                zoneView.SpChanged += sp => handle.Emit(new BotEvent(BotEventKind.Sp, sp));
                zoneView.Damaged += hit => handle.Emit(new BotEvent(BotEventKind.Hit, hit));
                var botId = handle.Id; // capture for the lambda

                // The selection died / went away — drop the assertion so the next attack re-sends TARGETTING
                zoneView.TargetInvalidated += why =>
                {
                    if (!handle.TargetAsserted) return;
                    handle.InvalidateTarget(why);
                    handle.BashTarget = 0;
                };
                zoneView.CastFailed += reason =>
                {
                    handle.Emit(new BotEvent(BotEventKind.CastFail, reason));
                    // PAIR THE FAILURE WITH THE GEOMETRY THAT CAUSED IT, on one line
                    var g = _lastCastGeom;
                    handle.Log(BotLogLevel.Info,
                        $"[castfail] 0x{reason:X4} ({ZoneView.CastFailReason.Describe(reason)}) " +
                        $"— dist={g.Dist:F0} reach={g.Range:F0} " +
                        $"offBy={(g.OffByDeg < 0 ? "n/a" : $"{g.OffByDeg:F0}°")} arc={g.ArcDeg}° ({g.Note})");
                    // Narrate the failure in game chat when watching. It carries the distance WE BELIEVED at the
                    // time, which is the number under suspicion: the operator can see it next to where the
                    // character actually is on screen.
                    Announce(handle, $"cast FAIL 0x{reason:X4} d={(g.Dist < 0 ? "?" : g.Dist.ToString("F0"))}u"
                        + (g.OffByDeg >= 0 ? $" off{g.OffByDeg:F0}°" : ""));
                    // NON-BATTLE MODE means the server disagrees with our belief, whatever we think. Drop it so the
                    // next EnsureBattleModeAsync re-sends CHANGEMODE_REQ instead of skipping it -- this is the
                    // result-packet feedback that keeps the optimistic model honest when something we do not model
                    // drops us out of battle mode.
                    if (reason == ZoneView.CastFailReason.NonBattleMode)
                    {
                        handle.InBattleMode = false;
                        handle.Log(BotLogLevel.Info,
                            "[combat] cast refused NON-BATTLE MODE (0x0FC0) — clearing battle-mode belief, re-asserting on the next action");
                    }
                    // Reactive cast-fail handling — lightweight, fire-and-forget
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
                        var tgt = handle.LastCastTarget;
                        var npcPos = tgt != 0 ? NpcPos(handle, tgt) : null;
                        if (npcPos is { } tp && handle.Position is { } pos)
                        {
                            // Use doubles for direction math to avoid uint underflow when tp.X < pos.X or tp.Y < pos.Y
                            double dx = (double)tp.X - pos.X;
                            double dy = (double)tp.Y - pos.Y;
                            var dist = Math.Sqrt(dx * dx + dy * dy);
                            var learnedRange = zoneView.LearnedMeleeRange;
                            var meleeStop = learnedRange > 0 ? learnedRange * 0.70 : ScenarioMeleeStop;  // final standoff (stop this short of the target)
                            var holdRange = learnedRange > 0 ? learnedRange * 0.85 : ScenarioHoldRange;  // within this → hold + autoAttack
                            var tooClose = learnedRange > 0 ? learnedRange * 0.40 : ScenarioTooClose;    // closer than this → step back
                            // In a SCENARIO INSTANCE, only client-approach a FAR target (a genuine traverse
                            if (zoneView.InScenarioInstance && dist < tooClose && dist > 0.5)
                            {
                                // TOO CLOSE → STEP BACK (operator 2026-07-15): we're basically on top of the mob (~1u), where the CAST fails 0x0…
                                var backX = -dx / dist; var backY = -dy / dist;   // unit vector AWAY from the target
                                var backDist = meleeStop - dist;                   // how far to retreat
                                var nx = (uint)Math.Round(pos.X + backX * backDist);
                                var ny = (uint)Math.Round(pos.Y + backY * backDist);
                                Log($"[combat] cast out of range (0x{reason:X4}) in instance, TOO CLOSE ({dist:F0}u) — stepping BACK to ~{meleeStop:F0}u standoff (learnedRange={learnedRange:F0}u; 1u overlap breaks the cast)");
                                _ = Task.Run(async () =>
                                {
                                    try { await WalkAsync(botId, pos.X, pos.Y, nx, ny, ct); }
                                    catch (Exception ex) { Log($"[combat] step-back error: {ex.Message}"); }
                                }, ct);
                            }
                            else if (zoneView.InScenarioInstance && (dist < holdRange || handle.MoveFailStreak >= 2))
                            {
                                // HOLD if EITHER we're already in melee (dist < holdRange) OR the approach is WEDGED (MoveFailStreak ≥ 2 = we've…
                                var why = dist < holdRange ? $"in melee ({dist:F0}u)" : $"WEDGED approaching (streak {handle.MoveFailStreak})";
                                Log($"[combat] cast out of range (0x{reason:X4}) in instance, {why} — HOLDING + autoAttack, letting the aggroing mob come (not chasing into a wall)");
                            }
                            else
                            {
                                // APPROACH but STOP at melee range (tick 41): walk to a point ~ScenarioMeleeStop SHORT of the target along the b…
                                // ALREADY INSIDE OUR OWN STOP RADIUS AND STILL "OUT OF RANGE" = A POSITION DESYNC,
                                // NOT A DISTANCE PROBLEM -- and the approach below cannot fix it, because
                                // dist - meleeStop clamps to 0 and walks us nowhere. That is a hard freeze: cast,
                                // fail, "approach" zero units, cast again, several times a second, indefinitely.
                                // Observed 16:45:32 at dist=35u with stop=70u after a mid-run root.
                                // Take a REAL step instead. The server then either accepts it (our belief was wrong
                                // and is now corrected) or MOVEFAILs (which snaps us to the truth) -- either way the
                                // desync resolves, which standing still can never do.
                                if (dist < meleeStop)
                                {
                                    Log($"[combat] cast rejected as OUT OF RANGE at {dist:F0}u while our own melee stop is " +
                                        $"{meleeStop:F0}u — we cannot be both. Treating it as a POSITION DESYNC and stepping to " +
                                        "force the server to correct us, rather than approaching zero units forever.");
                                    var (sx, sy) = (pos.X, pos.Y);
                                    var stepX = (uint)Math.Max(0, sx + (dx / dist) * DesyncProbeStep);
                                    var stepY = (uint)Math.Max(0, sy + (dy / dist) * DesyncProbeStep);
                                    // Fire-and-forget like the step-back branch: this handler is not async, and the
                                    // point is only to make the server answer, not to wait for it here.
                                    _ = Task.Run(() => WalkAsync(botId, sx, sy, stepX, stepY, ct), ct);
                                    return;
                                }
                                Log($"[combat] cast out of range (0x{reason:X4}) — approaching to melee (dist {dist:F0}u, stop {meleeStop:F0}u, learnedRange {learnedRange:F0}u)");
                                var goalDist = Math.Max(0.0, dist - meleeStop);
                                var gx = (uint)Math.Round(pos.X + dx / dist * goalDist);
                                var gy = (uint)Math.Round(pos.Y + dy / dist * goalDist);
                                var step = Math.Min(goalDist, MaxStepFor(120.0));
                                if (step > 0)
                                {
                                    var nx = (uint)Math.Round(pos.X + dx / dist * step);
                                    var ny = (uint)Math.Round(pos.Y + dy / dist * step);
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            // Route around obstacles via the .shbd grid (now corridor-centered); fall back to the straight step only if ther…
                                            var routed = false;
                                            if (handle.CurrentMap is { } cmap && GridProvider?.Invoke(cmap) is { } cgrid)
                                            {
                                                var path = PathFinder.FindPath(cgrid, pos.X, pos.Y, gx, gy);
                                                if (path.Count >= 2) { WalkPath(botId, PathFinder.Simplify(path)); routed = true; }
                                            }
                                            if (!routed) await WalkAsync(botId, pos.X, pos.Y, nx, ny, ct);
                                        }
                                        catch (Exception ex) { Log($"[combat] out-of-range approach error: {ex.Message}"); }
                                    }, ct);
                                }
                            }
                        }
                    }
                };
                using var buff = opt.Buff is { } buffCfg
                    ? new BuffInTownBehavior(zoneSession, zoneView, buffCfg, Log, ct)
                    : null;
                // ALWAYS ON, like party handling. Picking up something already at our feet is a reflex, not a
                // decision: it competes with nothing and there is no phase in which it is the wrong move. Leaving it
                // to the driver meant it only happened in phases that loot, so kills during a kite or a travel leg
                // were simply left on the ground. Walking to a drop stays with the driver, which is the only thing
                // that knows whether walking is a good idea right now.
                using var autoLoot = new Behaviors.AutoLootBehavior(
                    handle, zoneView,
                    h => PickupAsync(handle.Id, h, ct),
                    (lvl, m) => handle.Log(lvl, m), ct);

                handle.SetPhase(BotPhase.InZone);
                handle.ZoneEnteredUtc = DateTime.UtcNow;   // relog pacing measures session life from here
                handle.NoteEvent("zone-enter", $"map={currentMap}");
                // Entering a zone clears the server's selection AND renumbers handles — the retained one can name a different en…
                handle.InvalidateTarget("zone entry / map handoff");
                Log(firstEntry
                    ? $"*** {sel.Name} IN ZONE ({zoneEp}) — running until stopped ***"
                    : $"*** {sel.Name} RE-ENTERED ZONE ({zoneEp}, {currentMap}) after cross-server handoff ***");
                // The new zone link is live — let the leveler tick again (no-op unless we suspended it for a handoff above)
                handle.HandoffInFlight = false;   // cleared HERE and nowhere else: this is the first moment the link is usable
                handle.ScriptRunner?.Resume($"zone live again ({zoneEp}, {currentMap})");
                firstEntry = false;

                // ===== LIVENESS WATCHDOG (operator 2026-08-05) ===== "How do you need this elaborate diagnostic check to find o…
                _ = Task.Run(async () =>
                {
                    var lastPos = handle.Position; var stillSince = DateTime.UtcNow; var lastTicks = -1L;
                    var scriptRestarts = 0; var lastRestartUtc = DateTime.MinValue;
                    while (!zoneCts.IsCancellationRequested && handle.Phase == BotPhase.InZone)
                    {
                        await Task.Delay(WatchdogPollMs, zoneCts.Token);

                        // (a) IN ZONE WITH NO SCRIPT — the exact failure above
                        if (handle.ScriptRunner is null && handle.LastScriptSource is null
                            && (Knowledge?.LoadScript(handle.KnowledgeScope)
                                ?? Knowledge?.LoadScript(ScriptKeyForId(handle.Id))) is { } saved)
                        {
                            handle.LastScriptName = saved.Name;
                            handle.LastScriptSource = saved.Source;
                            handle.LastScriptTickMs = saved.TickMs;
                            handle.Log(BotLogLevel.Note,
                                $"WATCHDOG: no script in this process — restored '{saved.Name}' from durable " +
                                "knowledge (this bot ran it before a restart). Applying it.");
                        }
                        // (a2) THE RUNNER EXISTS BUT ITS THREAD IS DEAD (state=error after a FATAL). Branch (a) cannot
                        // see this — it only checks for a MISSING runner — so a crashed script used to sit there while
                        // (b) logged the same diagnosis forever: JcqArcher, 2026-08-18, ~60 reports over 50 minutes
                        // stood motionless being aggroed. Restarting is the job; reporting is not.
                        if (handle.ScriptRunner?.Status() is { State: "running", Ticks: > 500 }) scriptRestarts = 0;
                        if (handle.ScriptRunner?.Status() is { State: "error" } dead && handle.LastScriptSource is { } deadSrc)
                        {
                            var backoff = Math.Min(30 * Math.Pow(2, Math.Max(0, scriptRestarts - 1)), 300);
                            if ((DateTime.UtcNow - lastRestartUtc).TotalSeconds >= backoff)
                            {
                                scriptRestarts++; lastRestartUtc = DateTime.UtcNow;
                                handle.Log(BotLogLevel.Note,
                                    $"⛔ WATCHDOG: script '{dead.Name}' is DEAD (state=error, ticks={dead.Ticks}) — the FATAL was: " +
                                    $"{dead.LastError ?? "(none recorded)"}. Restarting it (attempt {scriptRestarts}).");
                                ApplyScript(handle.Id, handle.LastScriptName ?? "level_quest", deadSrc,
                                            handle.LastScriptTickMs <= 0 ? 400 : handle.LastScriptTickMs);
                                stillSince = DateTime.UtcNow;
                                continue;
                            }
                        }
                        if (handle.ScriptRunner is null && handle.LastScriptSource is { } src)
                        {
                            handle.Log(BotLogLevel.Note,
                                "⛔ WATCHDOG: IN ZONE but NO SCRIPT RUNNING — the leveler was lost (" +
                                "cause NOT established). Re-applying the last script.");
                            ApplyScript(handle.Id, handle.LastScriptName ?? "level_quest", src,
                                        handle.LastScriptTickMs <= 0 ? 400 : handle.LastScriptTickMs);
                            stillSince = DateTime.UtcNow;
                            continue;
                        }

                        // (b) NOT MOVING — position unchanged for too long
                        var pos = handle.Position;
                        var ticks = handle.ScriptRunner?.Status().Ticks ?? -1;
                        var moved = pos is null || lastPos is null
                                    || pos.Value.X != lastPos.Value.X || pos.Value.Y != lastPos.Value.Y;
                        if (moved) { lastPos = pos; stillSince = DateTime.UtcNow; lastTicks = ticks; continue; }

                        var stillFor = (DateTime.UtcNow - stillSince).TotalSeconds;
                        if (stillFor >= WatchdogStillSecs)
                        {
                            var ticking = ticks != lastTicks;
                            handle.Log(BotLogLevel.Note,
                                $"⛔ WATCHDOG: MOTIONLESS for {stillFor:F0}s at ({pos?.X},{pos?.Y}) on {handle.CurrentMap} — " +
                                $"script={(handle.ScriptRunner is null ? "NONE" : handle.ScriptRunner.Status().State)} " +
                                $"ticks={ticks} ({(ticking ? "ticking — it is RUNNING but not acting" : "NOT TICKING — thread dead/stuck/suspended")}) " +
                                $"hp={handle.ZoneView?.Hp} inCombat={handle.ZoneView?.InCombat} " +
                                // PRINT THE GATES, NOT JUST THE SYMPTOM
                                $"aggressors={handle.ZoneView?.Aggressors.Count} maybeAggressors={handle.ZoneView?.MaybeAggressors.Count} " +
                                $"hpStones={handle.ZoneView?.HpStones}. A bot standing still is ALWAYS a bug.");
                            stillSince = DateTime.UtcNow;   // re-arm so it reports periodically, not once
                        }
                        lastTicks = ticks;
                    }
                }, zoneCts.Token);

                // Run the zone read loop, but ALSO watch the WM read loop: if the WM link dies FIRST while the zone is still ali…
                var zoneRun = zoneSession.RunAsync(zoneCts.Token);
                if (await Task.WhenAny(zoneRun, wmRun) == wmRun
                    && !ct.IsCancellationRequested && !wmCts.IsCancellationRequested)
                {
                    Log($"WM link ended ({wmSession.State.DisconnectReason}) while zone alive — cancelling zone to reconnect");
                    zoneCts.Cancel();
                }
                await zoneRun; // let the zone loop finish (naturally, or via the cancel above)

                // KILL THE IN-FLIGHT WALK WITH THE SESSION IT WAS SENDING ON. WalkPath streams MOVERUN on a
                // background task whose CTS is linked to the BOT lifetime, not the zone session -- so a pod roll or
                // any reconnect left it stepping into a disposed connection:
                //   [conn] SEND AFTER DISPOSE op=0x2019 -- caller is using a dead connection
                //   walk-path error: send op=0x2019 after dispose
                // A walk is inherently per-session (it is a stream of moves on THAT link), so it must not outlive it.
                // TravelCts is deliberately NOT cancelled here: RunTravelAsync is written to survive a handoff and
                // waits for the zone to come back, which is the whole point of the re-entry gate in 355b51c.
                handle.WalkCts?.Cancel();

                // A captured cross-server handoff (and not a real stop) means reconnect to the carried endpoint with its WM hand…
                if (handoff is { IsCrossServer: true } ho && ho.Ip is { } ip && !ct.IsCancellationRequested)
                {
                    // TAKE ONLY THE PORT FROM THE HANDOFF, KEEP OUR CONFIGURED HOST
                    if (!string.Equals(ip, opt.Host, StringComparison.OrdinalIgnoreCase))
                        Log($"[nav] handoff advertised {ip}:{ho.Port} — connecting via configured host {opt.Host}:{ho.Port} instead");
                    zoneEp = new FiestaEndpoint(opt.Host, ho.Port);
                    zoneWmHandle = ho.WmHandle;
                    currentMap = handle.CurrentMap ?? currentMap;
                    // Let the WM→destination-zone handoff settle before we connect (see CrossServerHandoffSettleMs): connecting too…
                    handle.ScriptRunner?.Suspend($"cross-server handoff -> {zoneEp}");
                    Log($"[nav] reconnecting to zone {zoneEp} (wm={zoneWmHandle}) for cross-server handoff — settling {CrossServerHandoffSettleMs}ms for WM");
                    await Task.Delay(CrossServerHandoffSettleMs, ct);
                    continue;
                }

                Log($"zone session ended — {zoneSession.State.DisconnectReason}");
                break;
            }

            // The zone loop ended. Distinguish an INTENTIONAL stop (StopAsync set phase→Stopping, or the bot-wide ct was can…
            var unexpected = !ct.IsCancellationRequested && handle.Phase == BotPhase.InZone;
            if (unexpected)
            {
                handle.ScriptRunner?.Dispose(); handle.ScriptRunner = null; // stop the leveler ticking on a dead link
                using (var wmLogoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                    try { await wmSession.LogoutAsync(logoutReady: false, wmLogoutCts.Token); } catch { }
                wmCts.Cancel(); // unblock the WM read loop if the server didn't close it after our quit
            }
            try { await wmRun.WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (TimeoutException) { Log("wm read loop didn't end within 6s of teardown — forcing"); wmCts.Cancel(); }
            catch (OperationCanceledException) { }
            handle.SetPhase(BotPhase.Stopped);
            Log($"sessions ended — wm: {wmSession.State.DisconnectReason}");
            if (unexpected)
            {
                // How long did the session we just lost actually live?
                var lived = handle.ZoneEnteredUtc == DateTime.MinValue
                    ? TimeSpan.MaxValue
                    : DateTime.UtcNow - handle.ZoneEnteredUtc;
                if (lived < TimeSpan.FromSeconds(15)) handle.ShortSessionStreak++;
                else handle.ShortSessionStreak = 0;

                var waitS = handle.ShortSessionStreak switch
                {
                    0 => 0,          // healthy session dropped — resume immediately
                    1 => 10,
                    2 => 20,
                    3 => 40,
                    _ => 60,         // capped: keep trying, but stop hammering
                };
                if (waitS > 0)
                    Log($"⛔ CRITICAL: session lived only {lived.TotalSeconds:F0}s — that is the server still " +
                        $"holding the previous one, not a new fault. Waiting {waitS}s before relog " +
                        $"(short-session streak {handle.ShortSessionStreak}) instead of racing it again.");
                handle.NoteEvent("disconnect", $"livedSeconds={lived.TotalSeconds:F0} streak={handle.ShortSessionStreak}");
                Log("*** unexpected disconnect (zone link dropped; WM cleanly logged out — no ghost) — AUTO-RELOG to resume ***");
                if (waitS > 0) await Task.Delay(TimeSpan.FromSeconds(waitS));
                Relog(handle.Id);
            }
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

    /// <summary>Disposes the WM connection exactly once, even if it was never set (failure before the WM phase)</summary>
    private readonly struct FiestaClientConnectionScope(Net.FiestaClientConnection? conn) : IDisposable
    {
        public void Dispose() => conn?.Dispose();
    }
}
