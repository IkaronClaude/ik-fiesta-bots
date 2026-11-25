using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Net;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;

namespace Fiesta.Bot.Fleet;

/// <summary>Lifecycle states of a supervised bot.</summary>
public enum SupervisorState : byte { Offline, LoggingIn, World, InZone, Handoff, Paused, Stopping, Reconnecting, Failed }

/// <summary>Persisted configuration of one bot.</summary>
public sealed record BotRecord(string Id, string Username, string PasswordMd5, string Character, string Host, int LoginPort,
    string? Brain, string? Channel, string? VersionHash, bool Autostart, bool PacketLog)
{
    /// <summary>Packet set ("2016" / "2026").</summary>
    public string Wire { get; init; } = "2016";
    /// <summary>Create the character when missing.</summary>
    public bool Create { get; init; }
    /// <summary>Class id for creation.</summary>
    public int? ClassId { get; init; }
    /// <summary>World number.</summary>
    public int World { get; init; }
    /// <summary>Log levels (category -> level name), persisted.</summary>
    public Dictionary<string, string>? LogLevels { get; init; }
}

/// <summary>Summary row for the fleet page (API_SPEC 8.1 + UI 13.3).</summary>
public sealed record BotSummary(string Id, string State, string? Map, int Level, IReadOnlyDictionary<string, string> LayerStates,
    string? VersionHash, double TickP99Us, double ExpPerHour, double DeathsPerHour, string? LastError, string? Health)
{
    /// <summary>Character.</summary>
    public string Character { get; init; } = "";
    /// <summary>Economy over the last 30 minutes (per hour) and quest exp this session.</summary>
    public object? Econ { get; init; }
    /// <summary>Account.</summary>
    public string Username { get; init; } = "";
    /// <summary>Login host.</summary>
    public string Host { get; init; } = "";
    /// <summary>Login port.</summary>
    public int LoginPort { get; init; }
    /// <summary>Packet set.</summary>
    public string Wire { get; init; } = "2016";
    /// <summary>Base class.</summary>
    public int ClassFamily { get; init; }
    /// <summary>Brain name.</summary>
    public string? Brain { get; init; }
    /// <summary>Channel.</summary>
    public string? Channel { get; init; }
    /// <summary>Autostart.</summary>
    public bool Autostart { get; init; }
    /// <summary>Packet capture setting.</summary>
    public bool PacketLog { get; init; }
    /// <summary>Brain paused.</summary>
    public bool Paused { get; init; }
    /// <summary>Ticks per second.</summary>
    public double TicksPerSec { get; init; }
    /// <summary>When each layer's top-level state was entered.</summary>
    public IReadOnlyDictionary<string, DateTime> LayerSince { get; init; } = new Dictionary<string, DateTime>();
    /// <summary>Last transition.</summary>
    public object? LastTransition { get; init; }
    /// <summary>Vitals from the snapshot.</summary>
    public object? Vitals { get; init; }
    /// <summary>Version string brain@hash.</summary>
    public string? Version { get; init; }
}

/// <summary>Information about one brain version.</summary>
public sealed record BrainVersionInfo(string Hash, string Brain, int Api, DateTime Created, string Author, string Message,
    IReadOnlyList<string> Modules)
{
    /// <summary>Pinned libraries (path -> hash).</summary>
    public IReadOnlyDictionary<string, string> Libraries { get; init; } = new Dictionary<string, string>();
    /// <summary>Lint warnings at creation.</summary>
    public int Warnings { get; init; }
}

/// <summary>A stored brain version.</summary>
public sealed record StoredVersion(string Hash, string Brain, int Api, DateTime Created, string Author, string Message,
    string Definition, Dictionary<string, string> Modules, Dictionary<string, string> Libraries, List<LintResult> Lint);

/// <summary>A stored library version.</summary>
public sealed record LibraryVersion(string Hash, string Path, DateTime Created, string Author, string Message, string Source);

/// <summary>One apply of a brain version to a bot.</summary>
public sealed record AssignmentRecord(string Bot, DateTime At, string? FromHash, string ToHash, string By, string Reason, bool KeptState)
{
    /// <summary>Brain name.</summary>
    public string Brain { get; init; } = "";
}

/// <summary>Content-addressed store of brain and library versions, channels and assignments, as files under a data
/// directory (outside this repository; brains are private).</summary>
public sealed class BrainRepository
{
    private readonly string _dir;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Open (creates the directory).</summary>
    public BrainRepository(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(Path.Combine(dir, "brains"));
        Directory.CreateDirectory(Path.Combine(dir, "libraries"));
    }

    private static string Enc(string p) => Uri.EscapeDataString(p);
    private string BrainDir(string b) => Path.Combine(_dir, "brains", b);
    private string LibDir(string p) => Path.Combine(_dir, "libraries", Enc(p));

    /// <summary>Brain names.</summary>
    public IReadOnlyList<string> Brains() => Directory.GetDirectories(Path.Combine(_dir, "brains")).Select(Path.GetFileName).ToList()!;

    /// <summary>Versions of a brain, oldest first.</summary>
    public IReadOnlyList<StoredVersion> Versions(string brain)
    {
        var d = Path.Combine(BrainDir(brain), "versions");
        if (!Directory.Exists(d)) return [];
        return Directory.GetFiles(d, "*.json").Select(f => JsonSerializer.Deserialize<StoredVersion>(File.ReadAllText(f), Json)!).OrderBy(v => v.Created).ToList();
    }

    /// <summary>A version by hash or prefix (any brain when brain is null).</summary>
    public StoredVersion? Find(string? brain, string hash)
    {
        foreach (var b in brain is null ? Brains() : [brain])
            foreach (var v in Versions(b)) if (v.Hash == hash || (hash.Length >= 6 && v.Hash.StartsWith(hash))) return v;
        return null;
    }

    /// <summary>All module sources of a version (private modules + pinned libraries).</summary>
    public Dictionary<string, string> Sources(StoredVersion v)
    {
        var d = new Dictionary<string, string>(v.Modules);
        foreach (var (p, h) in v.Libraries) if (Library(p, h) is { } lv) d[p] = lv.Source;
        return d;
    }

    /// <summary>Compile a stored version.</summary>
    public CompiledBrain Load(StoredVersion v)
    {
        var def = BrainLoader.Parse(v.Definition);
        return new CompiledBrain(v.Hash, def, Sources(v), v.Lint);
    }

    /// <summary>Resolve library references (pinned or latest) of a definition.</summary>
    public Dictionary<string, string> ResolveLibraries(BrainDefinition def)
    {
        var libs = new Dictionary<string, string>();
        foreach (var m in def.Modules)
        {
            var path = BrainLinter.StripPin(m);
            if (path.StartsWith("brain/") || path.StartsWith("lib/")) continue;
            var pin = m.Contains('@') ? m[(m.IndexOf('@') + 1)..] : null;
            var list = LibraryVersions(path);
            var v = pin is null ? list.LastOrDefault() : list.FirstOrDefault(x => x.Hash.StartsWith(pin));
            if (v is not null) libs[path] = v.Hash;
        }
        return libs;
    }

    /// <summary>Lint a definition text and its modules.</summary>
    public (BrainDefinition? Def, List<LintResult> Lint, Dictionary<string, string> Libraries) Lint(string text, IReadOnlyDictionary<string, string> modules)
    {
        BrainDefinition def;
        try { def = BrainLoader.Parse(text); }
        catch (BrainFormatException e) { return (null, [new LintResult(LintSeverity.Error, "DEF_PARSE", e.Message, null, 1, null, null)], new()); }
        var libs = ResolveLibraries(def);
        var all = new Dictionary<string, string>(modules);
        foreach (var (p, h) in libs) if (Library(p, h) is { } lv) all[p] = lv.Source;
        return (def, BrainLinter.Lint(def, all, SensorRegistry.StaticPaths()).ToList(), libs);
    }

    /// <summary>Create a version (lint first); returns it or the lint errors.</summary>
    public (StoredVersion? Version, List<LintResult> Lint) Create(string brain, string text, IReadOnlyDictionary<string, string> modules, string author, string message)
    {
        var (def, lint, libs) = Lint(text, modules);
        if (def is not null && def.Brain != brain) lint.Add(new LintResult(LintSeverity.Error, "DEF_NAME", $"brain: '{def.Brain}' does not match '{brain}'", null, 1, null, null));
        if (def is null || lint.Any(l => l.Severity == LintSeverity.Error)) return (null, lint);
        var hashInput = text + "\0" + string.Join("\0", modules.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "\0" + k.Value))
                        + "\0" + string.Join("\0", libs.OrderBy(k => k.Key).Select(k => k.Key + "@" + k.Value)) + "\0" + ApiManifest.Version;
        var hash = BrainCompiler.ModuleHash(hashInput);
        lock (_lock)
        {
            if (Find(brain, hash) is { } existing) return (existing, lint);
            var v = new StoredVersion(hash, brain, def.Api, DateTime.UtcNow, author, message, text, new(modules), libs, lint);
            var d = Path.Combine(BrainDir(brain), "versions");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, hash + ".json"), JsonSerializer.Serialize(v, Json));
            return (v, lint);
        }
    }

    /// <summary>Channels of a brain.</summary>
    public Dictionary<string, string> Channels(string brain)
    {
        var f = Path.Combine(BrainDir(brain), "channels.json");
        return File.Exists(f) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f), Json) ?? new() : new();
    }
    /// <summary>Move a channel.</summary>
    public void SetChannel(string brain, string channel, string hash)
    {
        lock (_lock)
        {
            var c = Channels(brain); c[channel] = hash;
            Directory.CreateDirectory(BrainDir(brain));
            File.WriteAllText(Path.Combine(BrainDir(brain), "channels.json"), JsonSerializer.Serialize(c, Json));
        }
    }
    /// <summary>Resolve a channel to a hash.</summary>
    public string? ResolveChannel(string brain, string channel) => Channels(brain).GetValueOrDefault(channel);

    /// <summary>Library paths (stored, plus the host-provided lib/*).</summary>
    public IReadOnlyList<string> Libraries()
        => Directory.GetDirectories(Path.Combine(_dir, "libraries")).Select(d => Uri.UnescapeDataString(Path.GetFileName(d)!))
           .Concat(BuiltinLibraries.Sources.Keys).Distinct().OrderBy(x => x).ToList();
    /// <summary>Versions of a library, oldest first (lib/* has one: the host's).</summary>
    public IReadOnlyList<LibraryVersion> LibraryVersions(string path)
    {
        if (BuiltinLibraries.Sources.TryGetValue(path, out var src))
            return [new LibraryVersion(BrainCompiler.ModuleHash(src), path, Process.GetCurrentProcess().StartTime.ToUniversalTime(), "host", "host library", src)];
        var d = LibDir(path);
        if (!Directory.Exists(d)) return [];
        return Directory.GetFiles(d, "*.json").Select(f => JsonSerializer.Deserialize<LibraryVersion>(File.ReadAllText(f), Json)!).OrderBy(v => v.Created).ToList();
    }
    /// <summary>One library version.</summary>
    public LibraryVersion? Library(string path, string hash) => LibraryVersions(path).FirstOrDefault(v => v.Hash == hash || v.Hash.StartsWith(hash));
    /// <summary>Create a library module version.</summary>
    public LibraryVersion CreateLibraryVersion(string path, string source, string author, string message)
    {
        var hash = BrainCompiler.ModuleHash(source);
        lock (_lock)
        {
            if (Library(path, hash) is { } existing) return existing;
            var v = new LibraryVersion(hash, path, DateTime.UtcNow, author, message, source);
            Directory.CreateDirectory(LibDir(path));
            File.WriteAllText(Path.Combine(LibDir(path), hash + ".json"), JsonSerializer.Serialize(v, Json));
            return v;
        }
    }

    /// <summary>Record an assignment.</summary>
    public void RecordAssignment(AssignmentRecord record)
    {
        lock (_lock) File.AppendAllText(Path.Combine(_dir, "assignments.jsonl"), JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
    }
    /// <summary>Assignment history, newest first (filtered by bot and/or brain).</summary>
    public IReadOnlyList<AssignmentRecord> Assignments(string? bot = null, string? brain = null)
    {
        var f = Path.Combine(_dir, "assignments.jsonl");
        if (!File.Exists(f)) return [];
        return File.ReadAllLines(f).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AssignmentRecord>(l, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(a => (bot is null || a.Bot == bot) && (brain is null || a.Brain == brain)).Reverse().ToList();
    }
}

/// <summary>Persistent fleet configuration (a JSON file).</summary>
public sealed class FleetStore(string path)
{
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    /// <summary>All bot records.</summary>
    public List<BotRecord> All()
    {
        lock (_lock) return File.Exists(path) ? JsonSerializer.Deserialize<List<BotRecord>>(File.ReadAllText(path), Json) ?? new() : new();
    }
    /// <summary>Insert or update.</summary>
    public void Upsert(BotRecord r)
    {
        lock (_lock)
        {
            var all = All(); all.RemoveAll(x => x.Id == r.Id); all.Add(r);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
        }
    }
    /// <summary>Delete.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var all = All(); var n = all.RemoveAll(x => x.Id == id);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
            return n > 0;
        }
    }
}

/// <summary>Brain checkpoints for resume (files).</summary>
public sealed class CheckpointStore(string dir)
{
    /// <summary>Save.</summary>
    public void Save(BrainCheckpoint c) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, c.BotId + ".json"), JsonSerializer.Serialize(c)); }
    /// <summary>Latest for a bot.</summary>
    public BrainCheckpoint? Latest(string botId)
    {
        var f = Path.Combine(dir, botId + ".json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<BrainCheckpoint>(File.ReadAllText(f)) : null; } catch (JsonException) { return null; }
    }
}

/// <summary>K tick workers; each owns a partition of agents and ticks them at their interval. Agents are added to the
/// least loaded worker; removal takes effect between ticks.</summary>
public sealed class TickScheduler : IDisposable
{
    private sealed class Worker
    {
        public readonly List<BotAgent> Agents = new();
        public readonly ConcurrentQueue<(BotAgent A, bool Add, CountdownEvent? Done)> Changes = new();
        public double Busy; public Thread Thread = null!; public int Count;
    }
    private readonly Worker[] _w;
    private readonly int _intervalMs;
    private volatile bool _stop;
    /// <summary>Unhandled tick exceptions (agent id, error).</summary>
    public event Action<BotAgent, Exception>? TickFailed;

    /// <summary>Create with a worker count (default cores - 1) and base tick interval.</summary>
    public TickScheduler(int workers, TimeSpan interval)
    {
        _intervalMs = (int)interval.TotalMilliseconds;
        _w = new Worker[Math.Max(1, workers)];
        for (var i = 0; i < _w.Length; i++) _w[i] = new Worker();
    }
    /// <summary>Add an agent to the least loaded worker.</summary>
    public void Add(BotAgent agent) { var w = _w.OrderBy(x => x.Count).First(); Interlocked.Increment(ref w.Count); w.Changes.Enqueue((agent, true, null)); }
    /// <summary>Remove an agent.</summary>
    public void Remove(BotAgent agent) { foreach (var w in _w) w.Changes.Enqueue((agent, false, null)); }
    /// <summary>Remove an agent and wait until no worker ticks it any more (every worker took the change between two
    /// ticks), so its brain and state may be read and disposed safely. Not to be called from a tick worker.</summary>
    public bool RemoveAndWait(BotAgent agent, TimeSpan timeout)
    {
        if (_stop || Thread.CurrentThread.Name?.StartsWith("tick-", StringComparison.Ordinal) == true) { Remove(agent); return false; }
        using var done = new CountdownEvent(_w.Length);
        foreach (var w in _w) w.Changes.Enqueue((agent, false, done));
        return done.Wait(timeout);
    }
    /// <summary>Start the workers.</summary>
    public void Start()
    {
        for (var i = 0; i < _w.Length; i++)
        {
            var w = _w[i];
            w.Thread = new Thread(() => Loop(w)) { IsBackground = true, Name = $"tick-{i}", Priority = ThreadPriority.AboveNormal };
            w.Thread.Start();
        }
    }
    private void Loop(Worker w)
    {
        var sw = Stopwatch.StartNew();
        var next = sw.ElapsedMilliseconds;
        double busyAcc = 0; var window = sw.ElapsedMilliseconds;
        while (!_stop)
        {
            while (w.Changes.TryDequeue(out var c))
            {
                if (c.Add) { if (!w.Agents.Contains(c.A)) w.Agents.Add(c.A); }
                else if (w.Agents.Remove(c.A)) Interlocked.Decrement(ref w.Count);
                c.Done?.Signal();
            }
            var t0 = sw.ElapsedTicks;
            for (var i = 0; i < w.Agents.Count; i++)
            {
                var a = w.Agents[i];
                try { a.Tick(); }
                catch (Exception e) { TickFailed?.Invoke(a, e); }
            }
            busyAcc += (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (sw.ElapsedMilliseconds - window >= 1000) { w.Busy = busyAcc / (sw.ElapsedMilliseconds - window); busyAcc = 0; window = sw.ElapsedMilliseconds; }
            next += _intervalMs;
            var wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            else if (wait < -_intervalMs * 4) next = sw.ElapsedMilliseconds;
        }
    }
    /// <summary>Per-worker load (agents, busy fraction).</summary>
    public IReadOnlyList<(int Agents, double Busy)> Load => _w.Select(w => (w.Count, w.Busy)).ToList();
    /// <inheritdoc/>
    public void Dispose() => _stop = true;
}

/// <summary>Health problems the monitor raises (REDESIGN 10).</summary>
public enum HealthKind : byte { NoTick, TickOverBudget, StateOverBudget, NoProgress, TransitionCycle, Disconnected, GcPause }

/// <summary>One alert.</summary>
public sealed record HealthAlert(string Bot, HealthKind Kind, DateTime At, string Text, string? Layer, string? State);

/// <summary>Records a bot's inbound events, tick boundaries and control commands to a .botrec file (length-prefixed
/// binary records after a JSON header line).</summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly BinaryWriter _w;
    private readonly object _lock = new();
    /// <summary>File.</summary>
    public string Path { get; }
    /// <summary>Start recording to a file.</summary>
    public SessionRecorder(string path, string botId, string versionHash, string wire)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _w = new BinaryWriter(File.Create(path));
        var header = JsonSerializer.Serialize(new { format = "botrec", v = 1, bot = botId, version = versionHash, wire, api = ApiManifest.Version, started = DateTime.UtcNow });
        _w.Write(header);
    }
    /// <summary>Hook for <see cref="BotAgent.Recorder"/>.</summary>
    public void Record(long tick, long now, InboundEvent? e)
    {
        lock (_lock)
        {
            if (e is null) { _w.Write((byte)1); _w.Write(tick); _w.Write(now); return; }
            var x = e.Value;
            _w.Write((byte)2);
            _w.Write((ushort)x.Kind); _w.Write(x.Opcode); _w.Write(x.At); _w.Write(x.Handle); _w.Write(x.Handle2);
            _w.Write(x.A); _w.Write(x.B); _w.Write(x.C); _w.Write(x.X); _w.Write(x.Y);
            _w.Write(x.Text ?? ""); _w.Write((byte)(x.Options?.Length ?? 0));
            foreach (var o in x.Options ?? []) { _w.Write(o.Reply); _w.Write(o.Text); }
        }
    }
    /// <inheritdoc/>
    public void Dispose() { lock (_lock) _w.Dispose(); }
}

/// <summary>What a replay produced.</summary>
public sealed record ReplayResult(long Ticks, IReadOnlyList<(long Tick, ushort Opcode)> Intents, IReadOnlyList<TransitionRecord> Transitions,
    double TickP99Us, double AllocP95Bytes);

/// <summary>Replays a .botrec through an agent with the recorded clock; reports intents and transitions.</summary>
public sealed class SessionReplayer(string path)
{
    /// <summary>Header JSON.</summary>
    public JsonNode? Header { get; private set; }

    /// <summary>Run to the end (or a tick) and return what the agent emitted. The agent must have been built with a
    /// <see cref="ManualClock"/> (passed here) and its outbound ring link up.</summary>
    public ReplayResult Run(BotAgent agent, ManualClock clock, long untilTick = long.MaxValue)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        Header = JsonNode.Parse(r.ReadString());
        var intents = new List<(long, ushort)>();
        agent.Outbound.LinkUp = true;
        long ticks = 0; long tick = 0;
        var pending = new List<InboundEvent>();
        void Flush()
        {
            foreach (var e in pending) agent.Inbound.TryWrite(e);
            pending.Clear();
            agent.Tick(); ticks++;
            while (agent.Outbound.TryDequeue(out var op, out _)) intents.Add((tick, op));
        }
        var first = true;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var type = r.ReadByte();
            if (type == 1)
            {
                var t = r.ReadInt64(); var now = r.ReadInt64();
                if (!first) Flush();
                first = false;
                if (t > untilTick) break;
                tick = t; clock.NowMs = now;
            }
            else
            {
                var e = new InboundEvent
                {
                    Kind = (InboundKind)r.ReadUInt16(), Opcode = r.ReadUInt16(), At = r.ReadInt64(), Handle = r.ReadInt32(), Handle2 = r.ReadInt32(),
                    A = r.ReadInt64(), B = r.ReadInt64(), C = r.ReadInt64(), X = r.ReadUInt32(), Y = r.ReadUInt32(),
                };
                var text = r.ReadString(); e.Text = text.Length == 0 ? null : text;
                var n = r.ReadByte();
                if (n > 0) { e.Options = new (byte, string)[n]; for (var i = 0; i < n; i++) e.Options[i] = (r.ReadByte(), r.ReadString()); }
                pending.Add(e);
            }
        }
        if (!first) Flush();
        var brain = agent.Brain as BrainInstance;
        return new ReplayResult(ticks, intents, brain?.Debug.Transitions() ?? [], agent.Stats.RecentQuantile(x => x.TotalUs, 0.99),
            agent.Stats.RecentQuantile(x => x.AllocBytes, 0.95));
    }
}
