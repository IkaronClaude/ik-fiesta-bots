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
        Load();
        LoadQuestDeprio();
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
