using System.Collections.Concurrent;
using System.Text.Json;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Net;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Fleet;

/// <summary>Host-wide shared services and configuration.</summary>
public sealed class HostServices : IAsyncDisposable
{
    /// <summary>BYO cipher table.</summary>
    public required byte[] XorTable { get; init; }
    /// <summary>Client ressystem directory (tables, checksums).</summary>
    public required string RessystemDir { get; init; }
    /// <summary>Client root (resmap block files); defaults to the ressystem's parent.</summary>
    public required string ClientRoot { get; init; }
    /// <summary>Data directory (brains, fleet, checkpoints, logs, recordings) - outside the repository.</summary>
    public required string DataDir { get; init; }
    /// <summary>Default packet set.</summary>
    public WireKind DefaultWire { get; init; } = WireKind.Wire2016;
    /// <summary>Default login endpoint for the spawn form.</summary>
    public Endpoint DefaultLogin { get; init; } = new("127.0.0.1", 9010);
    /// <summary>Client data.</summary>
    public ClientData Data { get; private set; } = null!;
    /// <summary>Jobs.</summary>
    public JobScheduler Jobs { get; private set; } = null!;
    /// <summary>Navigation.</summary>
    public NavService Nav { get; private set; } = null!;
    /// <summary>Knowledge.</summary>
    public KnowledgeStore Knowledge { get; } = new();
    /// <summary>Clock.</summary>
    public IClock Clock { get; } = SystemClock.Instance;
    /// <summary>Log writer.</summary>
    public LogWriter Logs { get; private set; } = null!;
    /// <summary>Brain store.</summary>
    public BrainRepository Brains { get; private set; } = null!;
    /// <summary>Fleet store.</summary>
    public FleetStore Fleet { get; private set; } = null!;
    /// <summary>Checkpoints.</summary>
    public CheckpointStore Checkpoints { get; private set; } = null!;
    /// <summary>Tick workers.</summary>
    public TickScheduler Ticks { get; private set; } = null!;
    private readonly ConcurrentDictionary<WireKind, string[]> _checksums = new();
    private readonly ConcurrentDictionary<WireKind, DecoderTable> _tables = new();

    /// <summary>Start everything.</summary>
    public HostServices Start(int tickWorkers)
    {
        Directory.CreateDirectory(DataDir);
        Data = new ClientData(RessystemDir);
        Data.Preload();
        Jobs = new JobScheduler(Math.Max(1, Environment.ProcessorCount / 4));
        Nav = new NavService(ClientRoot, Jobs);
        Logs = new LogWriter(Path.Combine(DataDir, "logs"), new LogRetention());
        Logs.Start();
        Brains = new BrainRepository(Path.Combine(DataDir, "store"));
        Fleet = new FleetStore(Path.Combine(DataDir, "fleet.json"));
        Checkpoints = new CheckpointStore(Path.Combine(DataDir, "checkpoints"));
        var kn = Path.Combine(DataDir, "knowledge-npcs.json");
        if (File.Exists(kn)) Knowledge.ImportNpcs(File.ReadAllText(kn));
        LogRenderer.MobName = id => Data.Mob(id)?.Name;
        EntityDecoder.IsNpc = id => Data.Mob(id)?.IsNpc ?? false;
        PacketCapture.OpcodeName = op => FiestaLibReloaded.Networking.PacketRegistry.TryGetType(op, out var t) && t is not null ? t.Name : null;
        Ticks = new TickScheduler(tickWorkers, TimeSpan.FromMilliseconds(50));
        Ticks.Start();
        return this;
    }

    /// <summary>MAP_LOGIN checksums for a packet set (computed once).</summary>
    public string[] Checksums(WireProfile wire) => _checksums.GetOrAdd(wire.Kind, _ => wire.ComputeChecksums(RessystemDir));
    /// <summary>Decoder table for a packet set.</summary>
    public DecoderTable Decoders(WireProfile wire) => _tables.GetOrAdd(wire.Kind, _ => DecoderTable.CreateDefault(wire));

    /// <summary>Persist knowledge.</summary>
    public void SaveKnowledge() => File.WriteAllText(Path.Combine(DataDir, "knowledge-npcs.json"), Knowledge.ExportNpcs());

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Ticks.Dispose();
        Jobs.Dispose();
        SaveKnowledge();
        await Logs.DisposeAsync();
    }
}

/// <summary>One supervised bot: its record, logger, the current session's agent / brain / zone session, and the
/// lifecycle loop (login, zone, reconnect with backoff, clean logout).</summary>
public sealed class BotRuntime
{
    private readonly HostServices _h;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _stopRequested, _relogRequested;
    private long _lastFrameAt;
    private readonly Queue<DebugFrame> _history = new();
    private readonly Queue<object> _sensorHistory = new();
    /// <summary>Record.</summary>
    public BotRecord Record { get; set; }
    /// <summary>State.</summary>
    public SupervisorState State { get; private set; } = SupervisorState.Offline;
    /// <summary>Logger (survives sessions).</summary>
    public BotLogger Log { get; }
    /// <summary>Packet capture (survives sessions).</summary>
    public PacketCapture Capture { get; }
    /// <summary>The current agent (null offline).</summary>
    public BotAgent? Agent { get; private set; }
    /// <summary>The current brain (null without a brain).</summary>
    public BrainInstance? Brain { get; private set; }
    /// <summary>The current zone session.</summary>
    public ZoneSession? Session { get; private set; }
    /// <summary>Last error.</summary>
    public string? LastError { get; private set; }
    /// <summary>Latest debug frame (immutable).</summary>
    public DebugFrame? Frame { get; private set; }
    /// <summary>Latest sensor frame (immutable).</summary>
    public object? Sensors { get; private set; }
    /// <summary>Session start, exp at start.</summary>
    public DateTime SessionStart { get; private set; } = DateTime.UtcNow;
    /// <summary>Anyone watching debug/sensors (5 Hz frames); otherwise 1 Hz.</summary>
    public int Watchers;
    /// <summary>Raised with each new debug frame (any thread; must not block).</summary>
    public event Action<BotRuntime, DebugFrame>? FrameReady;
    /// <summary>Raised on each transition.</summary>
    public event Action<BotRuntime, TransitionRecord>? TransitionReady;
    /// <summary>Recorder (when recording).</summary>
    public SessionRecorder? Recorder { get; private set; }
    /// <summary>Recordings directory.</summary>
    public string RecordingsDir => Path.Combine(_h.DataDir, "recordings", Record.Id);
    /// <summary>Raised when an alert is due.</summary>
    public Action<BotRuntime, string, string>? Alert { get; set; }

    /// <summary>Create.</summary>
    public BotRuntime(BotRecord record, HostServices h)
    {
        _h = h; Record = record;
        Log = new BotLogger(record.Id, new LogRing(), h.Logs, h.Clock);
        if (record.LogLevels is { } lv)
            foreach (var (c, l) in lv)
                if (Enum.TryParse<LogCategory>(c, true, out var cat) && Enum.TryParse<LogLevel>(l, true, out var level)) Log.SetLevel(cat, level);
        Capture = new PacketCapture(Path.Combine(h.DataDir, "packets"), record.Id, h.Clock);
        if (record.PacketLog) Capture.Configure(PacketCaptureSettings.Off with { Enabled = true, Duration = TimeSpan.FromDays(3650) });
    }

    /// <summary>Recent debug frames (the in-memory "ring" recording).</summary>
    public IReadOnlyList<DebugFrame> History() { lock (_history) return _history.ToArray(); }
    /// <summary>Recent sensor frames.</summary>
    public IReadOnlyList<object> SensorHistory() { lock (_history) return _sensorHistory.ToArray(); }

    /// <summary>Start the lifecycle loop (no-op when running).</summary>
    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _stopRequested = false;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Stop: brain stop, flee/stop moving, clean logout.</summary>
    public async Task StopAsync(bool logout)
    {
        _stopRequested = true;
        State = SupervisorState.Stopping;
        if (Agent is { } a) a.Context.Log.Text(LogCategory.Core, LogLevel.Note, "stop requested");
        if (logout && Session is { } s)
        {
            Agent?.Brain?.Control(new ControlCommand(ControlKind.Pause), Agent.Context);
            await s.LogoutAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        _cts?.Cancel();
        if (_loop is not null) try { await _loop.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        State = SupervisorState.Offline;
    }

    /// <summary>Relog: checkpoint, logout, login, resume.</summary>
    public async Task RelogAsync()
    {
        _relogRequested = true;
        if (Session is { } s) await s.LogoutAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    /// <summary>Post a command to the tick.</summary>
    public bool Post(ControlCommand cmd)
    {
        if (Agent is not { } a) return false;
        a.ControlPost(cmd);
        return true;
    }

    /// <summary>Run a read on the tick thread.</summary>
    public async Task<object?> QueryAsync(Func<AgentContext, object?> q)
    {
        if (Agent is not { } a) throw new InvalidOperationException("offline");
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ControlPost(new ControlCommand(ControlKind.Action) { Query = q, Reply = tcs });
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Post a command and wait for its result.</summary>
    public async Task<object?> CommandAsync(ControlCommand cmd)
    {
        if (Agent is not { } a) throw new InvalidOperationException("offline");
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ControlPost(cmd with { Reply = tcs, Args = cmd.Args, Scope = cmd.Scope });
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Start recording inbound events to a .botrec file.</summary>
    public string StartRecording()
    {
        var path = Path.Combine(RecordingsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.botrec");
        Recorder = new SessionRecorder(path, Record.Id, Brain?.VersionHash ?? "", Record.Wire);
        if (Agent is { } a) a.Recorder = Recorder.Record;
        return path;
    }
    /// <summary>Stop recording.</summary>
    public void StopRecording() { if (Agent is { } a) a.Recorder = null; Recorder?.Dispose(); Recorder = null; }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = 5;
        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            try
            {
                State = SupervisorState.LoggingIn;
                LastError = null;
                var wire = WireProfile.For(WireProfile.Parse(Record.Wire));
                wire.ItemClassOf = _h.Data.ItemClass;
                Log.Text(LogCategory.Net, LogLevel.Note, $"login {Record.Username} @ {Record.Host}:{Record.LoginPort} ({wire.Name} wire), character {Record.Character}");
                var req = new LoginRequest(new Endpoint(Record.Host, Record.LoginPort), Record.Username, Record.PasswordMd5, Record.Character,
                    Record.Create, Record.ClassId)
                { World = (byte)Record.World, Log = s => Log.Text(LogCategory.Net, LogLevel.Info, s), Tap = (o, op, p) => Capture.OnFrame(o, op, p) };
                var login = await wire.CreateLogin(_h.XorTable).RunAsync(req, ct);
                State = SupervisorState.World;
                BuildSession(login, wire);
                State = SupervisorState.InZone;
                SessionStart = DateTime.UtcNow;
                backoff = 5;
                await Session!.RunAsync(login, ct);
                if (Session.Error is { } se) LastError = se.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                LastError = e.Message;
                Log.Text(LogCategory.Net, LogLevel.Crit, $"session failed: {e.Message}");
                Alert?.Invoke(this, "disconnect", e.Message);
            }
            finally { EndSession(); }
            if (_stopRequested || ct.IsCancellationRequested) break;
            if (_relogRequested) { _relogRequested = false; continue; }
            State = SupervisorState.Reconnecting;
            Log.Text(LogCategory.Net, LogLevel.Note, $"reconnecting in {backoff} s");
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(60, backoff * 2);
        }
        State = SupervisorState.Offline;
    }

    private void BuildSession(LoginResult login, WireProfile wire)
    {
        var inbound = new InboundRing();
        var outbound = new OutboundRing();
        var actions = new ActionSystem(outbound);
        var ctx = new AgentContext
        {
            BotId = Record.Id, Data = _h.Data, Nav = _h.Nav, Jobs = _h.Jobs, Knowledge = _h.Knowledge, Log = Log, Inbound = inbound, Actions = actions,
            World = new WorldState { Data = _h.Data, Knowledge = _h.Knowledge },
        };
        ctx.World.Self.Seed(login.CharacterName, login.ClassId, _h.Data.ClassFamily(login.ClassId), login.Level);
        ctx.Learners.Add(new AggroLearner());
        var agent = new BotAgent(ctx, inbound, outbound, new ControlQueue(), null, _h.Clock);
        agent.OnPublish = OnPublish;
        agent.Lifecycle = cmd => { if (cmd.Kind == ControlKind.Relog) _ = RelogAsync(); else _ = StopAsync(true); };
        if (Recorder is not null) agent.Recorder = Recorder.Record;
        if (ResolveBrain() is { } cb)
        {
            try
            {
                var brain = new BrainInstance(cb, ctx) { Resolver = h => _h.Brains.Find(Record.Brain, h) is { } v ? _h.Brains.Load(v) : null };
                brain.Swapped = (from, to) =>
                {
                    Record = Record with { VersionHash = to };
                    _h.Fleet.Upsert(Record);
                    _h.Brains.RecordAssignment(new AssignmentRecord(Record.Id, DateTime.UtcNow, from, to, "operator", "apply", true) { Brain = Record.Brain ?? "" });
                };
                brain.Debug.TransitionRecorded += r => TransitionReady?.Invoke(this, r);
                if (_h.Checkpoints.Latest(Record.Id) is { } cp && cp.VersionHash == cb.Hash) brain.Resume(cp);
                agent.Brain = brain;
                Brain = brain;
                if (Record.VersionHash != cb.Hash) { Record = Record with { VersionHash = cb.Hash }; _h.Fleet.Upsert(Record); }
            }
            catch (Exception e) { Log.Text(LogCategory.Brain, LogLevel.Crit, $"brain load failed: {e.Message}"); LastError = e.Message; }
        }
        Agent = agent;
        Session = new ZoneSession(wire, _h.Decoders(wire), _h.XorTable, inbound, outbound, _h.Checksums(wire), () => _h.Clock.NowMs,
            s => Log.Text(LogCategory.Net, LogLevel.Info, s)) { Tap = (o, op, p) => Capture.OnFrame(o, op, p) };
        _h.Ticks.Add(agent);
    }

    /// <summary>The brain version this bot should run: pinned hash, else channel, else latest.</summary>
    public CompiledBrain? ResolveBrain()
    {
        if (Record.Brain is not { } b) return null;
        var hash = Record.VersionHash ?? (Record.Channel is { } c ? _h.Brains.ResolveChannel(b, c) : null);
        var v = hash is not null ? _h.Brains.Find(b, hash) : _h.Brains.Versions(b).LastOrDefault();
        return v is null ? null : _h.Brains.Load(v);
    }

    private void EndSession()
    {
        if (Agent is { } a)
        {
            _h.Ticks.Remove(a);
            if (Brain is { } br)
            {
                try { _h.Checkpoints.Save(br.Checkpoint()); } catch (Exception e) { Log.Text(LogCategory.Brain, LogLevel.Note, $"checkpoint failed: {e.Message}"); }
            }
        }
        Brain?.Dispose();
        Brain = null;
        Agent = null;
        Session = null;
        _h.SaveKnowledge();
    }

    private void OnPublish(BotAgent a)
    {
        var now = a.Context.Now;
        var every = Watchers > 0 ? 200 : 1000;
        if (now - _lastFrameAt < every) return;
        _lastFrameAt = now;
        if (Brain is { } b)
        {
            b.Debug.Watched = Watchers > 0;
            var f = b.Frame(a.Context, a.Stats, a.Paused);
            Frame = f;
            var sf = Watchers > 0 || Sensors is null ? SensorFrames.Build(a) : null;
            if (sf is not null) Sensors = sf;
            lock (_history)
            {
                _history.Enqueue(f); if (_history.Count > 600) _history.Dequeue();
                if (sf is not null) { _sensorHistory.Enqueue(sf); if (_sensorHistory.Count > 600) _sensorHistory.Dequeue(); }
            }
            FrameReady?.Invoke(this, f);
        }
        else if (Watchers > 0 || Sensors is null) Sensors = SensorFrames.Build(a);
    }

    /// <summary>Apply a brain version (live when running: a hot swap at the next tick boundary).</summary>
    public async Task<AssignmentRecord> ApplyAsync(string brain, string hash, string? channel, bool keepState, string by, string reason)
    {
        var from = Record.VersionHash;
        var sameBrain = Record.Brain == brain;
        Record = Record with { Brain = brain, VersionHash = channel is null ? hash : null, Channel = channel };
        _h.Fleet.Upsert(Record);
        var rec = new AssignmentRecord(Record.Id, DateTime.UtcNow, from, hash, by, reason, keepState) { Brain = brain };
        _h.Brains.RecordAssignment(rec);
        if (Agent is { } a)
        {
            if (Brain is { } br && sameBrain)
                await CommandAsync(new ControlCommand(ControlKind.ApplyVersion, VersionHash: hash, Value: keepState ? "true" : "false", By: by));
            else if (_h.Brains.Find(brain, hash) is { } v)
            {
                var cb = _h.Brains.Load(v);
                await QueryAsync(ctx =>
                {
                    Brain?.Dispose();
                    var nb = new BrainInstance(cb, ctx) { Resolver = h2 => _h.Brains.Find(brain, h2) is { } vv ? _h.Brains.Load(vv) : null };
                    nb.Debug.TransitionRecorded += r => TransitionReady?.Invoke(this, r);
                    a.Brain = nb; Brain = nb;
                    return null;
                });
            }
        }
        Log.Text(LogCategory.Brain, LogLevel.Note, $"applied {brain}@{hash}{(channel is null ? "" : $" (channel {channel})")} keepState={keepState} by {by}");
        return rec;
    }
}

/// <summary>Owns every bot's lifecycle; the only writer of lifecycle state.</summary>
public sealed class BotSupervisor
{
    private readonly ConcurrentDictionary<string, BotRuntime> _bots = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Services.</summary>
    public HostServices Host { get; }
    /// <summary>Alerts (any thread).</summary>
    public event Action<string, string, string>? Alert;
    /// <summary>Bot added / removed / changed (fleet page).</summary>
    public event Action<BotRuntime>? BotChanged;

    /// <summary>Create and load the persisted fleet (autostart bots are started).</summary>
    public BotSupervisor(HostServices host)
    {
        Host = host;
        host.Ticks.TickFailed += (a, e) =>
        {
            a.Context.Log.Text(LogCategory.Core, LogLevel.Crit, $"tick failed: {e.GetType().Name}: {e.Message}");
            Alert?.Invoke(a.Context.BotId, "crit", e.Message);
        };
        foreach (var r in host.Fleet.All()) Add(r, r.Autostart);
    }

    private BotRuntime Add(BotRecord r, bool start)
    {
        var b = new BotRuntime(r, Host) { Alert = (bot, kind, text) => Alert?.Invoke(bot.Record.Id, kind, text) };
        _bots[r.Id] = b;
        if (start) b.Start();
        return b;
    }

    /// <summary>All bots.</summary>
    public IReadOnlyCollection<BotRuntime> Bots => _bots.Values.OrderBy(b => b.Record.Id).ToList();
    /// <summary>One bot.</summary>
    public BotRuntime? Get(string id) => _bots.GetValueOrDefault(id);

    /// <summary>Create (and optionally start) a bot.</summary>
    public BotRuntime Spawn(BotRecord record, bool start)
    {
        if (_bots.ContainsKey(record.Id)) throw new InvalidOperationException($"bot {record.Id} exists");
        Host.Fleet.Upsert(record);
        var b = Add(record, start);
        BotChanged?.Invoke(b);
        return b;
    }

    /// <summary>Update a bot's persisted configuration (applies at the next login).</summary>
    public void Update(BotRuntime b, BotRecord r) { b.Record = r; Host.Fleet.Upsert(r); BotChanged?.Invoke(b); }

    /// <summary>Stop and forget.</summary>
    public async Task ForgetAsync(string id)
    {
        if (_bots.TryRemove(id, out var b)) await b.StopAsync(true);
        Host.Fleet.Delete(id);
    }

    /// <summary>Drain for a deploy: checkpoint and log out every bot.</summary>
    public async Task DrainAsync() => await Task.WhenAll(_bots.Values.Select(b => b.StopAsync(true)));
}

/// <summary>Builds the sensor frame (API_SPEC 7.2 + UI U23) on the tick thread (only while someone watches).</summary>
public static class SensorFrames
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NavMesh, Dictionary<long, List<double[]>>> RectCache = new();

    // nav mesh rectangles within ~1,000 u, cached per 500 u cell (scanning every rectangle of a map is milliseconds)
    private static List<double[]> NavRects(NavMesh mesh, string map, double x, double y)
    {
        var cache = RectCache.GetOrCreateValue(mesh);
        var key = ((long)(x / 500) << 32) | (uint)(y / 500);
        lock (cache)
        {
            if (cache.TryGetValue(key, out var hit)) return hit;
            var (tx, ty) = BlockLayer.ToTile(x, y);
            var rects = new List<double[]>();
            foreach (var r in mesh.Rects)
                if (r.X + r.W >= tx - 160 && r.X <= tx + 160 && r.Y + r.H >= ty - 160 && r.Y <= ty + 160 && rects.Count < 400)
                    rects.Add([r.X * BlockLayer.WorldPerTile, r.Y * BlockLayer.WorldPerTile, r.W * BlockLayer.WorldPerTile, r.H * BlockLayer.WorldPerTile]);
            if (cache.Count > 256) cache.Clear();
            cache[key] = rects;
            return rects;
        }
    }

    /// <summary>Build.</summary>
    public static object Build(BotAgent a)
    {
        var c = a.Context; var w = c.World; var now = c.Now;
        var ents = new List<object>();
        for (var i = 0; i < w.Entities.Count; i++)
        {
            var s = w.Entities.LiveSlot(i);
            var k = w.KindOf(s, now);
            var (x, y) = w.Entities.Pos(s, now);
            var mob = w.Entities.MobId(s);
            var det = mob >= 0 ? c.Knowledge.Mob(mob, MobFact.DetectRadius) : Estimate.Unknown;
            var f = w.Entities.Facing(s);
            ents.Add(new
            {
                h = w.Entities.HandleOf(s),
                k = (k & EntityKind.Gate) != 0 ? "gate" : (k & EntityKind.Npc) != 0 ? "npc" : (k & EntityKind.Mob) != 0 ? "mob" : (k & EntityKind.Player) != 0 ? "player" : (k & EntityKind.Drop) != 0 ? "drop" : "other",
                mobId = mob, name = w.Entities.NameOf(s), x = Math.Round(x), y = Math.Round(y),
                hpPct = w.Entities.MaxHp(s) > 0 ? Math.Round(w.Entities.Hp(s) * 100 / w.Entities.MaxHp(s), 1) : (double?)null,
                aggro = (k & EntityKind.Aggro) != 0, dead = w.Entities.IsDead(s),
                detect = det.Samples > 0 ? det.Value : (double?)null,
                facing = f is { } ff ? new[] { Math.Round(ff.Dx, 2), Math.Round(ff.Dy, 2) } : null,
                leashOrigin = w.Entities.LastHitPos(s) is { } lh ? new[] { Math.Round(lh.X), Math.Round(lh.Y) } : null,
            });
        }
        var path = new List<double[]> { new[] { Math.Round(w.Self.Pos.X), Math.Round(w.Self.Pos.Y) } };
        foreach (var p in c.Move.Path) path.Add([p.X, p.Y]);
        var weapon = GameRules.Weapon(c);
        var skills = new List<object>();
        foreach (var id in w.Skills.Learned) if (c.Data.Skill(id) is { Range: > 0 } sk) skills.Add(new { id, reach = Math.Round(GameRules.SkillReach(sk, 0)) });
        object? nav = null;
        if (c.Nav.Mesh(w.Map.MapName) is { } mesh) nav = new { rects = NavRects(mesh, w.Map.MapName, w.Self.Pos.X, w.Self.Pos.Y) };
        return new
        {
            bot = c.BotId, map = w.Map.MapName, mapId = w.Map.MapId,
            self = new { x = Math.Round(w.Self.Pos.X), y = Math.Round(w.Self.Pos.Y), facing = new[] { Math.Round(w.Self.Facing.Dx, 2), Math.Round(w.Self.Facing.Dy, 2) } },
            entities = ents,
            reach = new { bash = Math.Round(GameRules.BashReach(GameRules.PlayerBody, weapon), 1), skills },
            path,
            nav,
        };
    }
}
