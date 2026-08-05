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

    public NpcKnowledge(string? dir = null)
    {
        var baseDir = dir
            ?? Environment.GetEnvironmentVariable("BOT_KNOWLEDGE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "bot-knowledge");
        _path = Path.Combine(baseDir, "npc-shops.json");
        _questDeprioPath = Path.Combine(baseDir, "quest-deprio.json");
        _mobThreatPath = Path.Combine(baseDir, "mob-threats.json");
        Load();
        LoadQuestDeprio();
        LoadMobThreat();
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

    private static string Key(string host, string map, int npcId) => $"{host}|{map}|{npcId}";

    /// <summary>The learnt shop kind of an NPC on a server+map, or null if never encountered.</summary>
    public string? ShopKind(string host, string map, int npcId) =>
        _shopKind.TryGetValue(Key(host, map, npcId), out var k) ? k : null;

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
