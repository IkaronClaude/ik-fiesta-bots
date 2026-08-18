using System.Collections.Concurrent;
using System.Text.Json;

namespace Fiesta.Bot.Manager;

/// <summary>Durable, per-server store of what the bot has LEARNT about NPCs by encountering them — primarily a shop classi…</summary>
public sealed class NpcKnowledge
{
    private readonly string _path;
    private readonly object _ioLock = new();
    // key = "host|map|npcId" -> kind ("weapon"|"skill"|"item"|"soulstone"|"notshop"|...)
    private readonly ConcurrentDictionary<string, string> _shopKind = new(StringComparer.Ordinal);

    private readonly string _questDeprioPath;
    private readonly object _questDeprioIoLock = new();
    // key = "host|questId" -> the character level at which a flee happened while pursuing it
    private readonly ConcurrentDictionary<string, int> _questDeprio = new(StringComparer.Ordinal);
    private readonly string _questDeathsPath;
    private readonly object _questDeathsIoLock = new();
    // key = "host|questId" -> how many times we have DIED pursuing it, across all sessions
    private readonly ConcurrentDictionary<string, int> _questDeaths = new(StringComparer.Ordinal);
    // key = "host|questId|L " -> deaths on that quest AT THAT CHARACTER LEVEL
    private readonly ConcurrentDictionary<string, int> _questDeathsAtLevel = new(StringComparer.Ordinal);

    public NpcKnowledge(string? dir = null)
    {
        var baseDir = dir
            ?? Environment.GetEnvironmentVariable("BOT_KNOWLEDGE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "bot-knowledge");
        _path = Path.Combine(baseDir, "npc-shops.json");
        _questDeprioPath = Path.Combine(baseDir, "quest-deprio.json");
        _questDeathsPath = Path.Combine(baseDir, "quest-deaths.json");
        _unstorablePath = Path.Combine(baseDir, "unstorable-items.json");
        _mobThreatPath = Path.Combine(baseDir, "mob-threats.json");
        _scalarPath = Path.Combine(baseDir, "learned-scalars.json");
        _scriptDir = Path.Combine(baseDir, "scripts");
        _rosterDir = Path.Combine(baseDir, "roster");   // spawn options per bot id — CREDENTIALS, never log/commit
        Load();
        LoadQuestDeprio();
        LoadQuestDeaths();
        LoadUnstorable();
        LoadMobThreat();
        LoadScalars();
    }

    private static string QKey(string host, int questId) => $"{host}|{questId}";

    /// <summary>The character level at which quest was deprioritized (a flee happened while pursuing its objective mob), or -1…</summary>
    public int QuestDeprioritizedAtLevel(string host, int questId) =>
        _questDeprio.TryGetValue(QKey(host, questId), out var lvl) ? lvl : -1;

    /// <summary>Record (and persist) that a flee happened while pursuing this quest's objective mob, at the given character le…</summary>
    public void RecordQuestDeprioritized(string host, int questId, int level)
    {
        if (string.IsNullOrEmpty(host)) return;
        var key = QKey(host, questId);
        if (_questDeprio.TryGetValue(key, out var ex) && ex == level) return; // already recorded, don't re-save
        _questDeprio[key] = level;
        SaveQuestDeprio();
    }

    private readonly string _unstorablePath;
    private readonly object _unstorableIoLock = new();
    // key = "host|itemId" -> true: the server REFUSED to put this item in storage (item-level, permanent)
    private readonly ConcurrentDictionary<string, bool> _unstorable = new(StringComparer.Ordinal);

    /// <summary>True if the server has already refused to STORE this item id</summary>
    public bool IsUnstorable(string host, int itemId) => _unstorable.ContainsKey(IKey(host, itemId));

    /// <summary>Record (and persist) that the server refuses to store this item id</summary>
    public void RecordUnstorable(string host, int itemId)
    {
        if (string.IsNullOrEmpty(host)) return;
        if (!_unstorable.TryAdd(IKey(host, itemId), true)) return;   // already known
        SaveUnstorable();
    }

    private static string IKey(string host, int itemId) => $"{host}|{itemId}";

    private void LoadUnstorable()
    {
        try
        {
            if (!File.Exists(_unstorablePath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_unstorablePath));
            if (d is not null) foreach (var (k, v) in d) if (v) _unstorable[k] = true;
        }
        catch { /* a corrupt/missing store just starts empty */ }
    }

    private void SaveUnstorable()
    {
        lock (_unstorableIoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_unstorablePath)!);
                File.WriteAllText(_unstorablePath, JsonSerializer.Serialize(
                    new SortedDictionary<string, bool>(_unstorable), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Clear a quest's flee-deprioritization</summary>
    public bool ClearQuestDeprioritized(string host, int questId)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (!_questDeprio.TryRemove(QKey(host, questId), out _)) return false;
        SaveQuestDeprio();
        return true;
    }

    /// <summary>Wipe EVERY quest penalty recorded for this scope: the flee-deprioritizations and the death counters
    /// behind them. Returns (deprioritizations, death counters) removed.
    /// Exists because a bug can poison this state faster than the game can clear it: until lua c6fa7af a single
    /// death was charged once per TICK while dead, so one death reached the "killed us 2x" limit on its own and
    /// deprioritized quests that were never actually failed. The marks persist until the character levels, and the
    /// character cannot level while every quest is marked -- a deadlock that needs an explicit repair.</summary>
    public (int Deprio, int Deaths) ClearAllQuestPenalties(string host)
    {
        if (string.IsNullOrEmpty(host)) return (0, 0);
        var prefix = host + "|";
        var deprio = 0;
        foreach (var k in _questDeprio.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && _questDeprio.TryRemove(k, out _)) deprio++;
        var deaths = 0;
        foreach (var k in _questDeaths.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && _questDeaths.TryRemove(k, out _)) deaths++;
        foreach (var k in _questDeathsAtLevel.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && _questDeathsAtLevel.TryRemove(k, out _)) deaths++;
        if (deprio > 0) SaveQuestDeprio();
        if (deaths > 0) SaveQuestDeaths();
        return (deprio, deaths);
    }

    /// <summary>Reset the PER-LEVEL death counter for a quest at the given level, so a cleared mark is not re-applied by the v…</summary>
    public int ClearQuestDeathsAtLevel(string host, int questId, int level)
    {
        if (string.IsNullOrEmpty(host) || level < 0) return 0;
        if (!_questDeathsAtLevel.TryRemove(LKey(host, questId, level), out var n)) return 0;
        SaveQuestDeaths();
        return n;
    }

    private readonly string _scriptDir;
    private readonly string _rosterDir;
    private readonly object _scriptIoLock = new();

    /// <summary>Remember the driver script a bot is running, so it can be restored after the PROCESS dies</summary>
    public void SaveScript(string scope, string name, string source, int tickMs)
    {
        if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(source)) return;
        lock (_scriptIoLock)
        {
            try
            {
                Directory.CreateDirectory(_scriptDir);
                File.WriteAllText(Path.Combine(_scriptDir, ScriptFile(scope)), JsonSerializer.Serialize(
                    new SavedScript(name, source, tickMs), new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { /* best-effort: never let persistence break an apply */ }
        }
    }

    public void SaveRosterEntry(string id, BotSpawnOptions opts)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_scriptIoLock)
        {
            try
            {
                Directory.CreateDirectory(_rosterDir);
                File.WriteAllText(Path.Combine(_rosterDir, ScriptFile(id)),
                    JsonSerializer.Serialize(opts, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Forget a bot: an EXPLICIT stop means the operator wants it stopped, and it must not come back on the next rest…</summary>
    public void ForgetRosterEntry(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_scriptIoLock)
        {
            try { File.Delete(Path.Combine(_rosterDir, ScriptFile(id))); } catch { }
        }
    }

    // PHASE TIME ACCOUNTING, DURABLY ─────────────────────────────────────────────────────────── Time-per-phase live…
    public string LogDir => Path.Combine(_rosterDir, "logs");

    public void SavePhaseSeconds(string id, IReadOnlyDictionary<string, double> phases)
    {
        if (string.IsNullOrWhiteSpace(id) || phases.Count == 0) return;
        lock (_scriptIoLock)
        {
            try
            {
                var dir = Path.Combine(_rosterDir, "phases");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, ScriptFile(id)), JsonSerializer.Serialize(phases));
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Phase totals carried over from previous runs of this bot, empty if none</summary>
    public IReadOnlyDictionary<string, double> LoadPhaseSeconds(string id)
    {
        try
        {
            var f = Path.Combine(_rosterDir, "phases", ScriptFile(id));
            if (File.Exists(f))
                return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(f))
                       ?? new Dictionary<string, double>();
        }
        catch { }
        return new Dictionary<string, double>();
    }

    /// <summary>Every bot that was running when the process last died, as (id, options)</summary>
    public IReadOnlyList<(string Id, BotSpawnOptions Options)> LoadRoster()
    {
        var outp = new List<(string, BotSpawnOptions)>();
        try
        {
            if (!Directory.Exists(_rosterDir)) return outp;
            foreach (var f in Directory.GetFiles(_rosterDir, "*.json"))
            {
                try
                {
                    var o = JsonSerializer.Deserialize<BotSpawnOptions>(File.ReadAllText(f));
                    var id = Path.GetFileNameWithoutExtension(f);
                    if (o is not null && !string.IsNullOrWhiteSpace(id)) outp.Add((id, o));
                }
                catch { /* skip an unreadable entry rather than lose the rest */ }
            }
        }
        catch { }
        return outp;
    }

    /// <summary>The last script this scope applied, or null if none was ever saved (a genuinely new bot)</summary>
    public SavedScript? LoadScript(string scope)
    {
        if (string.IsNullOrEmpty(scope)) return null;
        try
        {
            var f = Path.Combine(_scriptDir, ScriptFile(scope));
            return File.Exists(f) ? JsonSerializer.Deserialize<SavedScript>(File.ReadAllText(f)) : null;
        }
        catch { return null; }
    }

    // The scope contains '|' and a character name, neither of which is guaranteed path-safe
    private static string ScriptFile(string scope)
    {
        var sb = new System.Text.StringBuilder(scope.Length + 5);
        foreach (var ch in scope) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Append(".json").ToString();
    }

    /// <summary>How many times the bot has DIED while pursuing this quest, ever (across every session)</summary>
    public int QuestDeaths(string host, int questId) =>
        _questDeaths.TryGetValue(QKey(host, questId), out var n) ? n : 0;

    public int RecordQuestDeath(string host, int questId, int level = -1)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        // Lifetime total, kept for ranking (the risky-band sort wants "has this ever been deadly")
        var lifetime = _questDeaths.AddOrUpdate(QKey(host, questId), 1, (_, v) => v + 1);
        SaveQuestDeaths();
        if (level < 0) return lifetime;
        // PER-LEVEL count — this is what the deprioritize THRESHOLD must use
        var n = _questDeathsAtLevel.AddOrUpdate(LKey(host, questId, level), 1, (_, v) => v + 1);
        SaveQuestDeaths();
        return n;
    }

    private static string LKey(string host, int questId, int level) => $"{host}|{questId}|L{level}";

    private void LoadQuestDeaths()
    {
        try
        {
            if (!File.Exists(_questDeathsPath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_questDeathsPath));
            if (d is not null) foreach (var (k, v) in d)
                (k.Contains("|L") ? _questDeathsAtLevel : _questDeaths)[k] = v;
        }
        catch { /* a corrupt/missing store just starts empty */ }
    }

    private void SaveQuestDeaths()
    {
        lock (_questDeathsIoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_questDeathsPath)!);
                var all = new SortedDictionary<string, int>(_questDeaths);
                foreach (var (k, v) in _questDeathsAtLevel) all[k] = v;
                File.WriteAllText(_questDeathsPath, JsonSerializer.Serialize(
                    all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* persistence is best-effort; in-memory still works this session */ }
        }
    }

    private void LoadQuestDeprio()
    {
        try
        {
            if (!File.Exists(_questDeprioPath)) return;
            var json = File.ReadAllText(_questDeprioPath);
            var d = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (d is not null) foreach (var (k, v) in d) _questDeprio[k] = v;
        }
        catch { /* a corrupt/missing store just starts empty */ }
    }

    private void SaveQuestDeprio()
    {
        lock (_questDeprioIoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_questDeprioPath)!);
                var json = JsonSerializer.Serialize(
                    new SortedDictionary<string, int>(_questDeprio), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_questDeprioPath, json);
            }
            catch { /* persistence is best-effort; in-memory still works this session */ }
        }
    }

    private readonly string _mobThreatPath;
    private readonly object _mobThreatIoLock = new();
    // key = "host|mobId" -> observed damage samples
    private readonly ConcurrentDictionary<string, MobThreatSample> _mobThreat = new(StringComparer.Ordinal);

    /// <summary>Observed incoming-damage samples for one mob</summary>
    public sealed record MobThreatSample(int Max, int Count, long Sum);

    private static string MKey(string host, int mobId) => $"{host}|{mobId}";

    /// <summary>Everything learned about how hard hits on this server, or null if we have never been hit by it</summary>
    public MobThreatSample? MobThreat(string host, int mobId) =>
        _mobThreat.TryGetValue(MKey(host, mobId), out var s) ? s : null;

    /// <summary>All mob threat samples for a server, as mobId → sample (for seeding a fresh ZoneView)</summary>
    public IReadOnlyDictionary<int, MobThreatSample> MobThreatsFor(string host)
    {
        var prefix = host + "|";
        var outp = new Dictionary<int, MobThreatSample>();
        foreach (var (k, v) in _mobThreat)
            if (k.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(k.AsSpan(prefix.Length), out var mob)) outp[mob] = v;
        return outp;
    }

    public void RecordMobHit(string host, int mobId, int damage)
    {
        if (string.IsNullOrEmpty(host) || mobId <= 0 || damage <= 0) return;
        var key = MKey(host, mobId);
        var before = _mobThreat.TryGetValue(key, out var old) ? old.Max : -1;
        var upd = _mobThreat.AddOrUpdate(key,
            _ => new MobThreatSample(damage, 1, damage),
            (_, s) => new MobThreatSample(Math.Max(s.Max, damage), s.Count + 1, s.Sum + damage));
        if (upd.Max > before) SaveMobThreat();
    }

    private void LoadMobThreat()
    {
        try
        {
            if (!File.Exists(_mobThreatPath)) return;
            var json = File.ReadAllText(_mobThreatPath);
            var d = JsonSerializer.Deserialize<Dictionary<string, MobThreatSample>>(json);
            if (d is not null) foreach (var (k, v) in d) _mobThreat[k] = v;
        }
        catch { /* a corrupt/missing store just starts empty */ }
    }

    private void SaveMobThreat()
    {
        lock (_mobThreatIoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_mobThreatPath)!);
                var json = JsonSerializer.Serialize(
                    new SortedDictionary<string, MobThreatSample>(_mobThreat), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_mobThreatPath, json);
            }
            catch { /* persistence is best-effort; in-memory still works this session */ }
        }
    }

    private readonly string _scalarPath;
    private readonly object _scalarIoLock = new();
    private readonly ConcurrentDictionary<string, LearnedStat> _scalars = new(StringComparer.Ordinal);

    /// <summary>A learned measurement: the extremes plus enough to recompute a mean</summary>
    public sealed record LearnedStat(double Min, double Max, int Count, double Sum);

    private static string SKey(string host, string name) => $"{host}|{name}";

    public LearnedStat? Scalar(string host, string name) =>
        _scalars.TryGetValue(SKey(host, name), out var s) ? s : null;

    public void RecordScalar(string host, string name, double value)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(name) || double.IsNaN(value)) return;
        var key = SKey(host, name);
        var had = _scalars.TryGetValue(key, out var old);
        var upd = _scalars.AddOrUpdate(key,
            _ => new LearnedStat(value, value, 1, value),
            (_, s) => new LearnedStat(Math.Min(s.Min, value), Math.Max(s.Max, value), s.Count + 1, s.Sum + value));
        if (!had || upd.Min < old!.Min || upd.Max > old.Max) SaveScalars();
    }

    private void LoadScalars()
    {
        try
        {
            if (!File.Exists(_scalarPath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, LearnedStat>>(File.ReadAllText(_scalarPath));
            if (d is not null) foreach (var (k, v) in d) _scalars[k] = v;
        }
        catch { /* a corrupt/missing store just starts empty */ }
    }

    private void SaveScalars()
    {
        lock (_scalarIoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_scalarPath)!);
                File.WriteAllText(_scalarPath, JsonSerializer.Serialize(
                    new SortedDictionary<string, LearnedStat>(_scalars), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* persistence is best-effort; in-memory still works this session */ }
        }
    }

    private static string Key(string host, string map, int npcId) => $"{host}|{map}|{npcId}";

    /// <summary>The learnt shop kind of an NPC on a server+map, or null if never encountered</summary>
    public string? ShopKind(string host, string map, int npcId) =>
        _shopKind.TryGetValue(Key(host, map, npcId), out var k) ? k : null;

    /// <summary>Every NPC known to offer on this server, as (map, npcId)</summary>
    public IReadOnlyList<(string Map, int NpcId)> ShopsOfKind(string host, string kind)
    {
        var outp = new List<(string, int)>();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(kind)) return outp;
        var prefix = host + "|";
        foreach (var (key, k) in _shopKind)
        {
            if (!string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            // key = host|map|npcId — split from the RIGHT, so a map name containing '|' can't confuse it
            var lastBar = key.LastIndexOf('|');
            if (lastBar <= prefix.Length - 1) continue;
            if (!int.TryParse(key.AsSpan(lastBar + 1), out var npcId)) continue;
            outp.Add((key[prefix.Length..lastBar], npcId));
        }
        return outp;
    }

    /// <summary>Record (and persist) what an NPC's shop turned out to be</summary>
    public void RecordShop(string host, string map, int npcId, string kind)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(map) || string.IsNullOrEmpty(kind)) return;
        var key = Key(host, map, npcId);
        if (_shopKind.TryGetValue(key, out var ex) && ex == kind) return; // already known, don't re-save
        _shopKind[key] = kind;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (d is not null) foreach (var (k, v) in d) _shopKind[k] = v;
        }
        catch { /* a corrupt/missing store just starts empty — it re-learns */ }
    }

    private void Save()
    {
        lock (_ioLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(
                    new SortedDictionary<string, string>(_shopKind), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch { /* persistence is best-effort; in-memory still works this session */ }
        }
    }
}

/// <summary>A driver script persisted across process restarts — see</summary>
public sealed record SavedScript(string Name, string Source, int TickMs);
