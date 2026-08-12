using System.Collections.Concurrent;
using System.Text.Json;

namespace Fiesta.Bot.Manager;

/// <summary>
/// Durable, per-server store of what the bot has LEARNT about NPCs by encountering them — primarily a
/// shop classification (weapon / skill / item / soulstone / notshop) keyed by (server, map, npcId).
/// Persisted to disk so a town is classified ONCE EVER: after the first visit the bot walks straight to
/// the skill master / smith / healer with zero re-probing (re-probing the whole roster every relog —
/// ~seconds per quest NPC — was the main thing pinning the bot in town instead of grinding).
///
/// Keyed by SERVER (host) so different servers don't cross-contaminate (a future per-server knowledge
/// struct, tickets P3, can absorb this + the map/gate graph + mob-reachability). Thread-safe; saves are
/// debounced-by-write (each new fact triggers a save, cheap for this volume).
/// </summary>
public sealed class NpcKnowledge
{
    private readonly string _path;
    private readonly object _ioLock = new();
    // key = "host|map|npcId" -> kind ("weapon"|"skill"|"item"|"soulstone"|"notshop"|...)
    private readonly ConcurrentDictionary<string, string> _shopKind = new(StringComparer.Ordinal);

    private readonly string _questDeprioPath;
    private readonly object _questDeprioIoLock = new();
    // key = "host|questId" -> the character level at which a flee happened while pursuing it.
    private readonly ConcurrentDictionary<string, int> _questDeprio = new(StringComparer.Ordinal);
    private readonly string _questDeathsPath;
    private readonly object _questDeathsIoLock = new();
    // key = "host|questId" -> how many times we have DIED pursuing it, across all sessions.
    private readonly ConcurrentDictionary<string, int> _questDeaths = new(StringComparer.Ordinal);
    // key = "host|questId|L<level>" -> deaths on that quest AT THAT CHARACTER LEVEL. Persisted alongside
    // the lifetime totals in the same file; levelling up therefore gives a quest a genuinely clean slate.
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

    /// <summary>The character level at which quest <paramref name="questId"/> was deprioritized (a
    /// flee happened while pursuing its objective mob), or -1 if never / not currently deprioritized.
    /// The caller decides when this has expired (operator 2026-07-01: "after 1 level up, reset this") —
    /// this just stores the raw fact; compare against the CURRENT level to see if it still applies.</summary>
    public int QuestDeprioritizedAtLevel(string host, int questId) =>
        _questDeprio.TryGetValue(QKey(host, questId), out var lvl) ? lvl : -1;

    /// <summary>Record (and persist) that a flee happened while pursuing this quest's objective mob, at
    /// the given character level. Persisted so a rebuild/relog (this project's dev cycle resets Lua
    /// locals constantly) doesn't forget it and immediately re-trigger the same overwhelming fight.</summary>
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
    // key = "host|itemId" -> true: the server REFUSED to put this item in storage (item-level, permanent).
    private readonly ConcurrentDictionary<string, bool> _unstorable = new(StringComparer.Ordinal);

    /// <summary>True if the server has already refused to STORE this item id.
    /// <para>Some items simply cannot go in the warehouse — timed/bound ones like "Angel Wings (7 Days)"
    /// and "Ex Elreu". The refusal is a property of the ITEM, not of the slot or the moment, so retrying it
    /// can never succeed. Before this, every storage trip re-attempted the same two items and burned a 3s
    /// no-CELLCHANGE wait plus a CRUTCH[CRIT] line on each — noise in the very log the runbook says to read
    /// first.</para>
    /// <para>⚠️ Only record this for the item-level refusal code. A "cell occupied" refusal is transient
    /// and slot-level; treating it as permanent would blacklist a perfectly storable item forever.</para></summary>
    public bool IsUnstorable(string host, int itemId) => _unstorable.ContainsKey(IKey(host, itemId));

    /// <summary>Record (and persist) that the server refuses to store this item id.</summary>
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

    /// <summary>Clear a quest's flee-deprioritization. Returns true if a mark was actually removed.
    /// <para>Exists because LEVEL-UP was the ONLY expiry, and that is not enough: every mark is written at
    /// the level we fled at, so once a few quests get marked at the CURRENT level the whole board reads
    /// deprioritized and the bot drops to the last-resort grind — which is the slowest possible way to
    /// reach the level that would clear them. The operator hit exactly this on 2026-07-16 (stale level-21
    /// marks from a broken-combat era) and disabled the gate over it; it recurred on 2026-08-06 with six
    /// quests all marked at 26 while the bot sat at 26, because they were earned while combat was
    /// genuinely broken (LearnedMeleeRange had collapsed to 2u, damage dealt:taken was 1:76).</para>
    /// <para>So a mark must also expire on EVIDENCE: kill the quest's objective mob and you have proved the
    /// fight is winnable now, whatever was true when you fled.</para></summary>
    public bool ClearQuestDeprioritized(string host, int questId)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (!_questDeprio.TryRemove(QKey(host, questId), out _)) return false;
        SaveQuestDeprio();
        return true;
    }

    /// <summary>Reset the PER-LEVEL death counter for a quest at the given level, so a cleared mark is not
    /// re-applied by the very next death.
    /// <para>Needed for the operator's manual un-deprioritize. The mark and the counter are two separate
    /// facts: clearing only the mark leaves the counter sitting at or above the threshold, so one more
    /// death re-marks the quest instantly and the override reads as a no-op. Clearing the counter is what
    /// makes "give this quest another chance" actually mean a fresh chance.</para>
    /// <para>⚠️ Deliberately leaves the LIFETIME total alone. That total ranks a historically deadly quest
    /// lower and is evidence, not a gate — an override should re-open the decision, not erase what
    /// happened.</para>
    /// Returns the count that was cleared (0 if there was none).</summary>
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

    /// <summary>Remember the driver script a bot is running, so it can be restored after the PROCESS dies.
    /// <para>⛔ THIS IS THE HALF THAT WAS MISSING. <c>BotHandle.LastScript*</c> already let the in-process
    /// watchdog re-apply a lost script, but those fields live in process memory — a pod restart wipes them,
    /// and a freshly spawned bot has none. So every deploy left the bots running with NO DRIVER until a
    /// human re-applied it, and a bot with no script is not paused: it stands in the field and dies
    /// (operator 2026-08-06, raised above P0).</para>
    /// <para>Keyed by the caller's scope (host|character) so each character restores its own.</para></summary>
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

    /// <summary>Remember that a bot SHOULD BE RUNNING, so a pod restart can bring it back by itself.
    /// <para>⛔ THE OTHER HALF OF THE SAME HOLE. <see cref="SaveScript"/> persists WHAT a bot runs, but not
    /// THAT it exists — so after every restart the process came up with an empty roster and the whole test
    /// simply stopped, silently. Measured 2026-08-11: the host restarted FOUR times (deploys plus a node
    /// OOM) and each time `GET /api/bots` returned `[]` with nobody logged in and nothing saying so. An
    /// external watchdog script was standing in for this, which is not a fix — it only runs while a human
    /// is around to start it.</para>
    /// <para>⚠️ The spawn options carry CREDENTIALS, so this file is written to the knowledge PVC beside the
    /// other learned state and must never be logged or committed.</para></summary>
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

    /// <summary>Forget a bot: an EXPLICIT stop means the operator wants it stopped, and it must not come
    /// back on the next restart. Only StopAsync calls this — a crash or a pod kill leaves the entry, which
    /// is exactly the case we want restored.</summary>
    public void ForgetRosterEntry(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_scriptIoLock)
        {
            try { File.Delete(Path.Combine(_rosterDir, ScriptFile(id))); } catch { }
        }
    }

    // ── PHASE TIME ACCOUNTING, DURABLY ───────────────────────────────────────────────────────────
    // Time-per-phase lives on BotHandle, which is created fresh on every spawn — so RESPAWNING A BOT
    // ERASES IT. That is not hypothetical: five hours of accounting were destroyed on 2026-08-12 by
    // recovering two failed bots, minutes before the totals were due to be read. The whole point of the
    // metric is to see where a NIGHT goes, so it has to outlive respawns, deploys and pod restarts.
    // Keyed by bot id on the same persistent claim as the roster.
    /// <summary>Where per-bot tail files live — on the same persistent claim as the roster, so they
    /// survive pod restarts (an ephemeral container layer is wiped by every deploy).</summary>
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

    /// <summary>Phase totals carried over from previous runs of this bot, empty if none.</summary>
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

    /// <summary>Every bot that was running when the process last died, as (id, options).</summary>
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

    /// <summary>The last script this scope applied, or null if none was ever saved (a genuinely new bot).</summary>
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

    // The scope contains '|' and a character name, neither of which is guaranteed path-safe.
    private static string ScriptFile(string scope)
    {
        var sb = new System.Text.StringBuilder(scope.Length + 5);
        foreach (var ch in scope) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Append(".json").ToString();
    }

    /// <summary>How many times the bot has DIED while pursuing this quest, ever (across every
    /// session). 0 if never.</summary>
    public int QuestDeaths(string host, int questId) =>
        _questDeaths.TryGetValue(QKey(host, questId), out var n) ? n : 0;

    /// <summary>Record (and persist) a death while pursuing this quest; returns the new total.
    /// <para>Exists because the Lua driver's own <c>questDeaths</c> table is a script-local, wiped on
    /// every script re-apply — and this bot re-applies/respawns constantly, so the count that was
    /// supposed to rank a lethal quest LAST never survived to do it. Measured 2026-08-05: right after
    /// shipping a death-ranked ordering, the bot went straight back to the mob that had killed it and
    /// spent 53% of a 6-minute budget walking to it. Knowledge that resets every session cannot
    /// influence a bot that restarts every few minutes — so it lives here, on the PVC.</para></summary>
    public int RecordQuestDeath(string host, int questId, int level = -1)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        // Lifetime total, kept for ranking (the risky-band sort wants "has this ever been deadly").
        var lifetime = _questDeaths.AddOrUpdate(QKey(host, questId), 1, (_, v) => v + 1);
        SaveQuestDeaths();
        if (level < 0) return lifetime;
        // ⭐ PER-LEVEL count — this is what the deprioritize THRESHOLD must use.
        // The mark is level-scoped (RecordQuestDeprioritized carries the level), but the counter feeding
        // it was LIFETIME, so once a quest had 2 deaths EVER it was re-deprioritized on every subsequent
        // death, forever. q2564 had THIRTY recorded, most of them earned while combat was broken
        // (LearnedMeleeRange collapsed to 2u, damage dealt:taken 1:76) — so quests were being blamed for a
        // combat bug and the whole board went dark. Operator 2026-08-06: "they should not be
        // deprioritised in the first place". A level-scoped mark needs a level-scoped trigger.
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

    // ⚔️ DURABLE MOB THREAT TABLE — how hard each mob hits, learned by being hit.
    // ZoneView learns this per SESSION, but ZoneView is recreated on every respawn/pod-roll, and this bot
    // respawns constantly (deploys, deaths, cross-server handoffs). Measured 2026-08-05: mob4002 killed the
    // bot TWICE (208-246 dmg vs 881 maxHp = four hits, -528 exp) and in the very next session was not in the
    // table at all — so the lethality demotion built on it could not fire for the mob that motivated it.
    // Each session re-paid the learning cost the model exists to avoid paying twice. Persisting it here means
    // a mob is learned ONCE EVER, the same principle as the shop classification above.
    // ⚠️ Stores the SAMPLES (max/count/sum), never a derived verdict like "dangerous": maxHp changes with
    // levels and gear, so "how many hits can this kill me in" must be recomputed against CURRENT maxHp, not
    // frozen at the moment of first contact.
    private readonly string _mobThreatPath;
    private readonly object _mobThreatIoLock = new();
    // key = "host|mobId" -> observed damage samples
    private readonly ConcurrentDictionary<string, MobThreatSample> _mobThreat = new(StringComparer.Ordinal);

    /// <summary>Observed incoming-damage samples for one mob. <paramref name="Max"/> is the worst hit seen.</summary>
    public sealed record MobThreatSample(int Max, int Count, long Sum);

    private static string MKey(string host, int mobId) => $"{host}|{mobId}";

    /// <summary>Everything learned about how hard <paramref name="mobId"/> hits on this server, or null if we
    /// have never been hit by it. NULL MEANS NO EVIDENCE — it does NOT mean the mob is harmless.</summary>
    public MobThreatSample? MobThreat(string host, int mobId) =>
        _mobThreat.TryGetValue(MKey(host, mobId), out var s) ? s : null;

    /// <summary>All mob threat samples for a server, as mobId → sample (for seeding a fresh ZoneView).</summary>
    public IReadOnlyDictionary<int, MobThreatSample> MobThreatsFor(string host)
    {
        var prefix = host + "|";
        var outp = new Dictionary<int, MobThreatSample>();
        foreach (var (k, v) in _mobThreat)
            if (k.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(k.AsSpan(prefix.Length), out var mob)) outp[mob] = v;
        return outp;
    }

    /// <summary>Record one observed hit. Persists only when the WORST-CASE grows — the max is what drives the
    /// survivability decision, and saving on every sample would write on every incoming hit in combat.</summary>
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

    // 📊 DURABLE LEARNED SCALARS — per-server measurements that take REPEATED OBSERVATION to establish and
    // are therefore worthless if they reset every session. The HP-stone cooldown is the motivating case: it is
    // derived from the MINIMUM gap between two successful uses, so a fresh session cannot know it until the
    // bot has healed twice. Measured 2026-08-05: eight minutes into a session it was still unlearned and
    // SustainableHealDps still read -1 — i.e. the survivability inequality was unavailable for most of the
    // session, which is exactly when the bot is dying (deaths consumed 102% of exp over a 75-min window).
    // Keeps Min/Max/Count/Sum so a caller can use whichever statistic is correct for its quantity:
    //   · cooldown  → MIN (converges on the truth FROM ABOVE, so the smallest gap ever seen is the best estimate)
    //   · heal size → AVG/MAX
    // Stores raw samples, never a derived verdict — same rule as the mob threat table.
    private readonly string _scalarPath;
    private readonly object _scalarIoLock = new();
    private readonly ConcurrentDictionary<string, LearnedStat> _scalars = new(StringComparer.Ordinal);

    /// <summary>A learned measurement: the extremes plus enough to recompute a mean.</summary>
    public sealed record LearnedStat(double Min, double Max, int Count, double Sum);

    private static string SKey(string host, string name) => $"{host}|{name}";

    /// <summary>A learned scalar for this server, or null if never measured. NULL = NO EVIDENCE.</summary>
    public LearnedStat? Scalar(string host, string name) =>
        _scalars.TryGetValue(SKey(host, name), out var s) ? s : null;

    /// <summary>Record one observation. Persists only when an EXTREME moves (min down or max up) — the
    /// extremes are what the callers key off, and saving every sample would write on every heal.</summary>
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

    /// <summary>The learnt shop kind of an NPC on a server+map, or null if never encountered.</summary>
    public string? ShopKind(string host, string map, int npcId) =>
        _shopKind.TryGetValue(Key(host, map, npcId), out var k) ? k : null;

    /// <summary>Every NPC known to offer <paramref name="kind"/> on this server, as (map, npcId).
    /// <para>Exists because <c>ShopKind</c> can only answer "what is this NPC, HERE" — so the driver could
    /// never ask the question it actually needed: <b>which map has a storage keeper?</b> Without that it
    /// could not travel to one, and the storage trip only ran if the bot happened to already be standing
    /// in the right town. Found 2026-08-06 while root-causing the bag-full hand-in deadlock.</para></summary>
    public IReadOnlyList<(string Map, int NpcId)> ShopsOfKind(string host, string kind)
    {
        var outp = new List<(string, int)>();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(kind)) return outp;
        var prefix = host + "|";
        foreach (var (key, k) in _shopKind)
        {
            if (!string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            // key = host|map|npcId — split from the RIGHT, so a map name containing '|' can't confuse it.
            var lastBar = key.LastIndexOf('|');
            if (lastBar <= prefix.Length - 1) continue;
            if (!int.TryParse(key.AsSpan(lastBar + 1), out var npcId)) continue;
            outp.Add((key[prefix.Length..lastBar], npcId));
        }
        return outp;
    }

    /// <summary>Record (and persist) what an NPC's shop turned out to be. No-op if unchanged.</summary>
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

/// <summary>A driver script persisted across process restarts — see <see cref="NpcKnowledge.SaveScript"/>.</summary>
public sealed record SavedScript(string Name, string Source, int TickMs);
