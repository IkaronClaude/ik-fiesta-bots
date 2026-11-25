using System.Collections.Concurrent;
using System.Diagnostics;

namespace Fiesta.Bot.Services;

/// <summary>Marks a method as tick-path code checked by the allocation analyzer (FBOT002).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HotPathAttribute : Attribute;

/// <summary>Marks a member of a tick-path assembly that may block or read wall time (FBOT001 does not apply): code that
/// runs off the tick, or rare tick work (a brain swap).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Constructor)]
public sealed class OffTickAttribute : Attribute;

/// <summary>Monotonic clock; the tick reads it once (replayable).</summary>
public interface IClock
{
    /// <summary>Milliseconds since an arbitrary epoch.</summary>
    long NowMs { get; }
    /// <summary>Wall-clock time for display.</summary>
    DateTime UtcNow { get; }
}

/// <summary>Real clock.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance.</summary>
    public static readonly SystemClock Instance = new();
    private static readonly long Start = Stopwatch.GetTimestamp();
    private static readonly DateTime StartUtc = DateTime.UtcNow;
    /// <inheritdoc/>
    public long NowMs => (long)Stopwatch.GetElapsedTime(Start).TotalMilliseconds;
    /// <inheritdoc/>
    public DateTime UtcNow => DateTime.UtcNow;
    /// <summary>Wall time of a monotonic stamp.</summary>
    public static DateTime ToUtc(long ms) => StartUtc.AddMilliseconds(ms);
    private static readonly long StartUnixMs = new DateTimeOffset(StartUtc).ToUnixTimeMilliseconds();
    /// <summary>Unix milliseconds of a monotonic stamp.</summary>
    public static long UnixMs(long ms) => StartUnixMs + ms;
}

/// <summary>A clock the test or the replayer moves.</summary>
public sealed class ManualClock : IClock
{
    /// <summary>Now.</summary>
    public long NowMs { get; set; }
    /// <inheritdoc/>
    public DateTime UtcNow => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(NowMs);
}

/// <summary>Client MobInfo row (the fields the runtime uses).</summary>
public sealed record MobInfoRow(int Id, string InxName, string Name, int Level, int MaxHp, int WalkSpeed, int RunSpeed,
    bool IsNpc, int AbsoluteSize, int WeaponType, int GradeType, int Type, bool IsPlayerSide);

/// <summary>Client ActiveSkill row (the fields the runtime uses).</summary>
public sealed record ActiveSkillRow(int Id, string InxName, string Name, int Step, int Range, int Area, int TargetNumber,
    int CastTimeMs, int DelayMs, int DelayGroup, int DelayGroupMs, int Sp, int Hp, bool IsMovingSkill, int UsableDegree,
    int DemandType, int SkillTargetState, int EffectType, int UseClass, int MinWc, int MaxWc, int MinMa, int MaxMa,
    string Family, string StaNameA, string StaNameB, string StaNameC, string StaNameD)
{
    /// <summary>First special effect (index, value): index 1 = heal amount.</summary>
    public int SpecialIndexA { get; init; }
    /// <summary>Value of the first special effect.</summary>
    public int SpecialValueA { get; init; }
    /// <summary>Maximum rank of the family.</summary>
    public int MaxStep { get; init; }
}

/// <summary>Client ItemInfo row.</summary>
public sealed record ItemInfoRow(int Id, string InxName, string Name, int Type, int ItemClass, int Equip, int DemandLevel,
    int WeaponType, int SellPrice, int BuyPrice, int MaxLot, int UseClass, string ItemUseSkill);

/// <summary>Client MapInfo row.</summary>
public sealed record MapInfoRow(int Id, string MapName, string Name, bool IsInstance, int RegenX, int RegenY);

/// <summary>A quest definition parsed from QuestData.</summary>
public sealed record QuestDef(int Id, string Name, int MinLevel, int MaxLevel, bool Repeatable, int StartNpc,
    IReadOnlyList<QuestObjectiveDef> Objectives, long Exp, int PrevQuest, int StartClass, int EndClass, string Script);

/// <summary>One quest objective.</summary>
public sealed record QuestObjectiveDef(QuestObjectiveKind Kind, int MobId, int ItemId, int Need);

/// <summary>Objective kinds.</summary>
public enum QuestObjectiveKind : byte { Kill, Collect, Talk, ReachLevel, Other }

/// <summary>Read-only client data (ressystem SHN), loaded once and shared.</summary>
public interface IClientData
{
    /// <summary>MobInfo by id, null if absent (0 is a valid id).</summary>
    MobInfoRow? Mob(int id);
    /// <summary>ActiveSkill by id.</summary>
    ActiveSkillRow? Skill(int id);
    /// <summary>ItemInfo by id.</summary>
    ItemInfoRow? Item(int id);
    /// <summary>MapInfo by id.</summary>
    MapInfoRow? Map(int id);
    /// <summary>MapInfo by short name.</summary>
    MapInfoRow? Map(string mapName);
    /// <summary>Quest by id.</summary>
    QuestDef? Quest(int id);
    /// <summary>QuestDialog text by dialog id.</summary>
    string? Dialog(int id);
    /// <summary>Base class of a class (the first class of its line, from UseClassTypeInfo), -1 unknown.</summary>
    int ClassFamily(int classId);
    /// <summary>Class name.</summary>
    string ClassName(int classId);
    /// <summary>All quests.</summary>
    IReadOnlyCollection<QuestDef> Quests { get; }
    /// <summary>The ressystem directory.</summary>
    string Dir { get; }
}

/// <summary>Client data loaded from a ressystem directory. Tables load lazily, once; rows are immutable.</summary>
public sealed class ClientData : IClientData
{
    private readonly Lazy<Dictionary<int, MobInfoRow>> _mobs;
    private readonly Lazy<Dictionary<int, ActiveSkillRow>> _skills;
    private readonly Lazy<Dictionary<int, ItemInfoRow>> _items;
    private readonly Lazy<(Dictionary<int, MapInfoRow> ById, Dictionary<string, MapInfoRow> ByName)> _maps;
    private readonly Lazy<Dictionary<int, string>> _dialogs;
    private readonly Lazy<Dictionary<int, QuestDef>> _quests;
    private readonly Lazy<Dictionary<long, string>> _texts;
    private readonly Lazy<(int[] Family, string[] Names)> _classes;

    /// <inheritdoc/>
    public string Dir { get; }

    /// <summary>Create over a ressystem directory.</summary>
    public ClientData(string ressystemDir)
    {
        Dir = ressystemDir;
        _texts = new(LoadTexts);
        _mobs = new(LoadMobs);
        _skills = new(LoadSkills);
        _items = new(LoadItems);
        _maps = new(LoadMaps);
        _dialogs = new(LoadDialogs);
        _quests = new(LoadQuests);
        _classes = new(LoadClasses);
    }

    private ShnTable? T(string name)
    {
        var p = Path.Combine(Dir, name + ".shn");
        return File.Exists(p) ? ShnTable.Load(p) : null;
    }

    private string Name(ShnTable t, int row, string col)
    {
        var c = t.Col(col);
        if (c < 0) return "";
        // the US client ships untranslated names as question marks ("?? ??"): no name, the caller falls back to the InxName
        if (t.Str(row, c) is { } s) return s.Contains('?') && s.All(ch => ch == '?' || char.IsWhiteSpace(ch)) ? "" : s;
        var id = t.Num(row, c);   // 2026 tables carry a text id here
        return _texts.Value.TryGetValue(id, out var text) ? text : "";
    }

    private Dictionary<long, string> LoadTexts()
    {
        var d = new Dictionary<long, string>();
        foreach (var n in new[] { "TextData", "TextData2", "TextData3" })
            if (T(n) is { } t)
                for (var r = 0; r < t.RowCount; r++) d.TryAdd(t.Num(r, "eTextID"), t.Str(r, "acString") ?? "");
        return d;
    }

    private Dictionary<int, MobInfoRow> LoadMobs()
    {
        var d = new Dictionary<int, MobInfoRow>();
        if (T("MobInfo") is not { } t) return d;
        for (var r = 0; r < t.RowCount; r++)
        {
            var inx = t.Str(r, "InxName") ?? "";
            var name = Name(t, r, "Name");
            d[(int)t.Num(r, "ID")] = new MobInfoRow((int)t.Num(r, "ID"), inx, name.Length > 0 ? name : inx, (int)t.Num(r, "Level"),
                (int)t.Num(r, "MaxHP"), (int)t.Num(r, "WalkSpeed"), (int)t.Num(r, "RunSpeed"), t.Num(r, "IsNPC") != 0,
                (int)t.Num(r, "AbsoluteSize"), (int)t.Num(r, "WeaponType"), (int)t.Num(r, "GradeType"), (int)t.Num(r, "Type"),
                t.Num(r, "IsPlayerSide") != 0);
        }
        return d;
    }

    /// <summary>Skill family = InxName without its trailing rank digits.</summary>
    public static string FamilyOf(string inx)
    {
        var n = inx.Length;
        while (n > 0 && char.IsDigit(inx[n - 1])) n--;
        return inx[..n];
    }

    private Dictionary<int, ActiveSkillRow> LoadSkills()
    {
        var d = new Dictionary<int, ActiveSkillRow>();
        if (T("ActiveSkill") is not { } t) return d;
        for (var r = 0; r < t.RowCount; r++)
        {
            var inx = t.Str(r, "InxName") ?? "";
            var nm = Name(t, r, "Name");
            int I(string c) => (int)t.Num(r, c);
            d[I("ID")] = new ActiveSkillRow(I("ID"), inx, nm.Length > 0 ? nm : inx, I("Step"), I("Range"), I("Area"), I("TargetNumber"),
                I("CastTime"), I("DlyTime"), I("DlyGroupNum"), I("DlyTimeGroup"), I("SP"), I("HP"), I("IsMovingSkill") != 0,
                I("UsableDegree"), I("DemandType"), I("SkillTargetState"), I("EffectType"), I("UseClass"),
                I("MinWC"), I("MaxWC"), I("MinMA"), I("MaxMA"), FamilyOf(inx),
                t.Str(r, "StaNameA") ?? "-", t.Str(r, "StaNameB") ?? "-", t.Str(r, "StaNameC") ?? "-", t.Str(r, "StaNameD") ?? "-")
            { SpecialIndexA = I("SpecialIndexA"), SpecialValueA = I("SpecialValueA"), MaxStep = I("MaxStep") };
        }
        return d;
    }

    private Dictionary<int, ItemInfoRow> LoadItems()
    {
        var d = new Dictionary<int, ItemInfoRow>();
        if (T("ItemInfo") is not { } t) return d;
        for (var r = 0; r < t.RowCount; r++)
        {
            int I(string c) => (int)t.Num(r, c);
            var inx = t.Str(r, "InxName") ?? "";
            var nm = Name(t, r, "Name");
            d[I("ID")] = new ItemInfoRow(I("ID"), inx, nm.Length > 0 ? nm : inx, I("Type"), I("Class"), I("Equip"), I("DemandLv"),
                I("WeaponType"), I("SellPrice"), I("BuyPrice"), I("MaxLot"), I("UseClass"), t.Str(r, "ItemUseSkill") ?? "-");
        }
        return d;
    }

    private (Dictionary<int, MapInfoRow>, Dictionary<string, MapInfoRow>) LoadMaps()
    {
        var a = new Dictionary<int, MapInfoRow>(); var b = new Dictionary<string, MapInfoRow>(StringComparer.OrdinalIgnoreCase);
        if (T("MapInfo") is not { } t) return (a, b);
        for (var r = 0; r < t.RowCount; r++)
        {
            var m = new MapInfoRow((int)t.Num(r, "ID"), t.Str(r, "MapName") ?? "", Name(t, r, "Name"), t.Num(r, "InSide") != 0,
                (int)t.Num(r, "RegenX"), (int)t.Num(r, "RegenY"));
            a[m.Id] = m; b.TryAdd(m.MapName, m);
        }
        return (a, b);
    }

    private Dictionary<int, string> LoadDialogs()
    {
        var d = new Dictionary<int, string>();
        if (T("QuestDialog") is not { } t) return d;
        for (var r = 0; r < t.RowCount; r++) d[(int)t.Num(r, "ID")] = t.Str(r, "Dialog") ?? "";
        return d;
    }

    private Dictionary<int, QuestDef> LoadQuests()
    {
        var d = new Dictionary<int, QuestDef>();
        if (T("QuestData") is not { } t || t.Col("Script") < 0) return d;   // the 2016 client's bespoke QuestData: not read yet
        var obj = new Dictionary<int, List<QuestObjectiveDef>>();
        void Add(int q, QuestObjectiveDef o) { if (!obj.TryGetValue(q, out var l)) obj[q] = l = new(); l.Add(o); }
        if (T("QuestEndNpc") is { } en)
            for (var r = 0; r < en.RowCount; r++)
            {
                if (en.Num(r, "IsEnabled") == 0) continue;
                var n = (int)en.Num(r, "Count");
                Add((int)en.Num(r, "ID"), new QuestObjectiveDef(n > 0 ? QuestObjectiveKind.Kill : QuestObjectiveKind.Talk, (int)en.Num(r, "MobID"), -1, Math.Max(1, n)));
            }
        // QuestAction: (quest, condition 1 = a kill of mob ConditionTarget) -> (result 1 = the item ResultTarget): the mob
        // that drops a quest's collect item
        var dropper = new Dictionary<(int Quest, int Item), int>();
        if (T("QuestAction") is { } qa)
            for (var r = 0; r < qa.RowCount; r++)
                if (qa.Num(r, "Condition") == 1 && qa.Num(r, "Result") == 1)
                    dropper.TryAdd(((int)qa.Num(r, "ID"), (int)qa.Num(r, "ResultTarget")), (int)qa.Num(r, "ConditionTarget"));
        if (T("QuestEndItem") is { } ei)
            for (var r = 0; r < ei.RowCount; r++)
            {
                int q = (int)ei.Num(r, "ID"), item = (int)ei.Num(r, "ItemID");
                Add(q, new QuestObjectiveDef(QuestObjectiveKind.Collect, dropper.TryGetValue((q, item), out var mob) ? mob : -1, item, (int)ei.Num(r, "Count")));
            }
        var exp = new Dictionary<int, long>();
        if (T("QuestReward") is { } rw)
            for (var r = 0; r < rw.RowCount; r++)
                if (rw.Num(r, "RewardType") == 0) exp[(int)rw.Num(r, "ID")] = rw.Num(r, "Flag");
        for (var r = 0; r < t.RowCount; r++)
        {
            int I(string c) => (int)t.Num(r, c);
            var id = I("ID");
            var name = Dialog(I("NameId"))?.Trim() ?? $"quest {id}";
            d[id] = new QuestDef(id, name, I("LvlStart"), I("LvlEnd"), I("RepeatType") != 0, I("NPCID"),
                obj.TryGetValue(id, out var o) ? o : Array.Empty<QuestObjectiveDef>(), exp.GetValueOrDefault(id), I("PrevQuestID"),
                I("StartClassID"), I("EndClassID"), t.Str(r, "Script") ?? "");
        }
        return d;
    }

    // a class line = the largest contiguous run of classes (at most five) that one UseClassTypeInfo row allows
    private (int[], string[]) LoadClasses()
    {
        var names = new string[64]; var fam = new int[64];
        Array.Fill(names, ""); Array.Fill(fam, -1);
        if (T("ClassName") is { } cn)
            for (var r = 0; r < cn.RowCount; r++) { var id = (int)cn.Num(r, "ClassID"); if (id is >= 0 and < 64) names[id] = cn.Str(r, "acEngName") ?? ""; }
        var best = new int[64];
        if (T("UseClassTypeInfo") is { } u)
        {
            var classCols = Enumerable.Range(1, u.Columns.Length - 1).ToArray();   // column k = class k
            for (var r = 0; r < u.RowCount; r++)
            {
                var set = classCols.Where(c => u.Num(r, c) != 0).ToArray();
                if (set.Length is 0 or > 5) continue;
                var contiguous = set[^1] - set[0] + 1 == set.Length;
                if (!contiguous) continue;
                foreach (var c in set)
                    if (c < 64 && set.Length > best[c]) { best[c] = set.Length; fam[c] = set[0]; }
            }
        }
        return (fam, names);
    }

    /// <inheritdoc/>
    public MobInfoRow? Mob(int id) => _mobs.Value.GetValueOrDefault(id);
    /// <inheritdoc/>
    public ActiveSkillRow? Skill(int id) => _skills.Value.GetValueOrDefault(id);
    /// <inheritdoc/>
    public ItemInfoRow? Item(int id) => _items.Value.GetValueOrDefault(id);
    /// <inheritdoc/>
    public MapInfoRow? Map(int id) => _maps.Value.ById.GetValueOrDefault(id);
    /// <inheritdoc/>
    public MapInfoRow? Map(string mapName) => _maps.Value.ByName.GetValueOrDefault(mapName);
    /// <inheritdoc/>
    public QuestDef? Quest(int id) => _quests.Value.GetValueOrDefault(id);
    /// <inheritdoc/>
    public string? Dialog(int id) => _dialogs.Value.GetValueOrDefault(id);
    /// <inheritdoc/>
    public int ClassFamily(int classId) => classId is >= 0 and < 64 ? _classes.Value.Family[classId] : -1;
    /// <inheritdoc/>
    public string ClassName(int classId) => classId is >= 0 and < 64 ? _classes.Value.Names[classId] : "";
    /// <inheritdoc/>
    public IReadOnlyCollection<QuestDef> Quests => _quests.Value.Values;
    /// <summary>All maps.</summary>
    public IReadOnlyCollection<MapInfoRow> Maps => _maps.Value.Item1.Values;
    /// <summary>All skills.</summary>
    public IReadOnlyCollection<ActiveSkillRow> Skills => _skills.Value.Values;
    /// <summary>Item class lookup for the 2026 item-record translation.</summary>
    public int ItemClass(int id) => Item(id)?.ItemClass ?? -1;
    /// <summary>Force-load everything (startup, off the tick).</summary>
    public void Preload() { _ = _mobs.Value; _ = _skills.Value; _ = _items.Value; _ = _maps.Value; _ = _quests.Value; _ = _classes.Value; }
}

/// <summary>Job kinds that run off the tick.</summary>
public enum JobKind : byte { Path, RouteLength, QuestPlan, TravelPlan, SafeSpot, Custom }

/// <summary>Handle to a job.</summary>
public readonly record struct JobTicket(int Value)
{
    /// <summary>Rejected / none.</summary>
    public static readonly JobTicket None = new(-1);
}

/// <summary>Bounded worker pool for off-tick work; completions are posted to the owning bot through a callback.</summary>
public interface IJobScheduler
{
    /// <summary>Submit work; never blocks (rejects with <see cref="JobTicket.None"/> when saturated).</summary>
    JobTicket Submit(int botIndex, JobKind kind, Func<CancellationToken, object?> work, Action<JobTicket, bool>? done = null);
    /// <summary>Result of a completed job, null while pending.</summary>
    object? Result(JobTicket ticket);
    /// <summary>Is the job finished?</summary>
    bool IsDone(JobTicket ticket);
    /// <summary>Cancel if not started.</summary>
    bool Cancel(JobTicket ticket);
    /// <summary>Forget a result (frees it).</summary>
    void Forget(JobTicket ticket);
}

/// <summary>Default job pool: a fixed number of worker threads over a bounded queue.</summary>
public sealed class JobScheduler : IJobScheduler, IDisposable
{
    private sealed class Job { public int Id; public Func<CancellationToken, object?> Work = null!; public Action<JobTicket, bool>? Done; public volatile bool Cancelled; }
    private readonly System.Threading.Channels.Channel<Job> _queue;
    private readonly ConcurrentDictionary<int, (bool Done, object? Result)> _results = new();
    private readonly CancellationTokenSource _cts = new();
    private int _next;
    private readonly Thread[] _workers;
    /// <summary>Jobs run.</summary>
    public long Completed;

    /// <summary>Create with workers and a queue bound.</summary>
    public JobScheduler(int workers = 2, int capacity = 4096)
    {
        _queue = System.Threading.Channels.Channel.CreateBounded<Job>(new System.Threading.Channels.BoundedChannelOptions(capacity) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        _workers = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            _workers[i] = new Thread(Loop) { IsBackground = true, Name = $"jobs-{i}", Priority = ThreadPriority.BelowNormal };
            _workers[i].Start();
        }
    }

    private void Loop()
    {
        var reader = _queue.Reader;
        while (!_cts.IsCancellationRequested)
        {
            Job job;
            try { if (!reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult()) return; } catch { return; }
            if (!reader.TryRead(out job!)) continue;
            if (job.Cancelled) { _results.TryRemove(job.Id, out _); continue; }
            object? r = null; var ok = true;
            try { r = job.Work(_cts.Token); } catch (Exception e) { r = e; ok = false; }
            _results[job.Id] = (true, r);
            Interlocked.Increment(ref Completed);
            job.Done?.Invoke(new JobTicket(job.Id), ok);
        }
    }

    private readonly ConcurrentDictionary<int, Job> _pending = new();

    /// <inheritdoc/>
    public JobTicket Submit(int botIndex, JobKind kind, Func<CancellationToken, object?> work, Action<JobTicket, bool>? done = null)
    {
        var id = Interlocked.Increment(ref _next);
        var job = new Job { Id = id, Work = work, Done = done };
        _results[id] = (false, null);
        if (!_queue.Writer.TryWrite(job)) { _results.TryRemove(id, out _); return JobTicket.None; }
        _pending[id] = job;
        return new JobTicket(id);
    }
    /// <inheritdoc/>
    public object? Result(JobTicket t) => _results.TryGetValue(t.Value, out var r) && r.Done ? r.Result : null;
    /// <inheritdoc/>
    public bool IsDone(JobTicket t) => _results.TryGetValue(t.Value, out var r) && r.Done;
    /// <inheritdoc/>
    public bool Cancel(JobTicket t) { if (_pending.TryRemove(t.Value, out var j)) { j.Cancelled = true; return true; } return false; }
    /// <inheritdoc/>
    public void Forget(JobTicket t) { _results.TryRemove(t.Value, out _); _pending.TryRemove(t.Value, out _); }
    /// <inheritdoc/>
    public void Dispose() { _cts.Cancel(); _queue.Writer.TryComplete(); }
}

/// <summary>An interval estimate learned from observations.</summary>
public sealed record Estimate(double Value, double Low, double High, int Samples)
{
    /// <summary>Nothing known.</summary>
    public static readonly Estimate Unknown = new(-1, -1, -1, 0);
}

/// <summary>What a mob estimator tracks.</summary>
public enum MobFact : byte { DetectRadius, ChaseDistance, Reach, AttackInterval, Armour, MagicResist, HitAvg, HitMax }

/// <summary>Host-wide learned knowledge (server-only facts estimated from the wire), persisted and shared.</summary>
public interface IKnowledgeStore
{
    /// <summary>Current estimate for a mob fact.</summary>
    Estimate Mob(int mobId, MobFact fact);
    /// <summary>Add a lower bound observation (e.g. noticed us at distance d).</summary>
    void ObserveAtLeast(int mobId, MobFact fact, double value);
    /// <summary>Add an upper bound observation (e.g. did not notice us at distance d).</summary>
    void ObserveAtMost(int mobId, MobFact fact, double value);
    /// <summary>Add a direct sample (an interval, a hit).</summary>
    void Sample(int mobId, MobFact fact, double value);
    /// <summary>Family assist radius.</summary>
    Estimate Assist(int familyId);
    /// <summary>NPC role mask learned from menus.</summary>
    int NpcRole(int npcId);
    /// <summary>Record an NPC role.</summary>
    void LearnNpcRole(int npcId, int roleMask, int mapId, uint x, uint y);
    /// <summary>Known NPC of a role on a map: (npc mob id, x, y) or null.</summary>
    (int NpcId, uint X, uint Y)? KnownNpc(int roleMask, int mapId);
}

/// <summary>In-memory knowledge store with interval estimators, optionally persisted to a JSON file.</summary>
public sealed class KnowledgeStore : IKnowledgeStore
{
    private sealed class Acc
    {
        public double Lo = double.NaN, Hi = double.NaN, Sum; public int N, Bounds;
        public Estimate Get()
        {
            if (N == 0 && Bounds == 0) return Estimate.Unknown;
            var v = N > 0 ? Sum / N : !double.IsNaN(Lo) && !double.IsNaN(Hi) ? (Lo + Hi) / 2 : !double.IsNaN(Lo) ? Lo : Hi;
            return new Estimate(v, double.IsNaN(Lo) ? v : Lo, double.IsNaN(Hi) ? v : Hi, N + Bounds);
        }
    }
    private readonly ConcurrentDictionary<(int, MobFact), Acc> _mob = new();
    private readonly ConcurrentDictionary<int, Acc> _assist = new();
    private readonly ConcurrentDictionary<int, (int Mask, int Map, uint X, uint Y)> _npc = new();

    /// <inheritdoc/>
    public Estimate Mob(int mobId, MobFact fact) => _mob.TryGetValue((mobId, fact), out var a) ? a.Get() : Estimate.Unknown;
    /// <inheritdoc/>
    public void ObserveAtLeast(int mobId, MobFact fact, double value)
    {
        var a = _mob.GetOrAdd((mobId, fact), _ => new Acc());
        lock (a) { a.Lo = double.IsNaN(a.Lo) ? value : Math.Max(a.Lo, value); a.Bounds++; }
    }
    /// <inheritdoc/>
    public void ObserveAtMost(int mobId, MobFact fact, double value)
    {
        var a = _mob.GetOrAdd((mobId, fact), _ => new Acc());
        lock (a) { a.Hi = double.IsNaN(a.Hi) ? value : Math.Min(a.Hi, value); a.Bounds++; }
    }
    /// <inheritdoc/>
    public void Sample(int mobId, MobFact fact, double value)
    {
        var a = _mob.GetOrAdd((mobId, fact), _ => new Acc());
        lock (a)
        {
            a.Sum += value; a.N++;
            a.Lo = double.IsNaN(a.Lo) ? value : Math.Min(a.Lo, value);
            a.Hi = double.IsNaN(a.Hi) ? value : Math.Max(a.Hi, value);
        }
    }
    /// <inheritdoc/>
    public Estimate Assist(int familyId) => _assist.TryGetValue(familyId, out var a) ? a.Get() : Estimate.Unknown;
    /// <summary>Add an assist sample.</summary>
    public void SampleAssist(int familyId, double d) { var a = _assist.GetOrAdd(familyId, _ => new Acc()); lock (a) { a.Sum += d; a.N++; } }
    /// <inheritdoc/>
    public int NpcRole(int npcId) => _npc.TryGetValue(npcId, out var r) ? r.Mask : 0;
    /// <inheritdoc/>
    public void LearnNpcRole(int npcId, int roleMask, int mapId, uint x, uint y)
        => _npc.AddOrUpdate(npcId, (roleMask, mapId, x, y), (_, o) => (o.Mask | roleMask, mapId, x, y));
    /// <inheritdoc/>
    public (int NpcId, uint X, uint Y)? KnownNpc(int roleMask, int mapId)
    {
        foreach (var kv in _npc) if ((kv.Value.Mask & roleMask) != 0 && kv.Value.Map == mapId) return (kv.Key, kv.Value.X, kv.Value.Y);
        return null;
    }
    /// <summary>Snapshot for persistence and the knowledge endpoint.</summary>
    public object Snapshot(int mobId) => Enum.GetValues<MobFact>().ToDictionary(f => f.ToString(), f => Mob(mobId, f));

    /// <summary>Serialise NPC roles (JSON).</summary>
    public string ExportNpcs() => System.Text.Json.JsonSerializer.Serialize(_npc.Select(k => new { id = k.Key, mask = k.Value.Mask, map = k.Value.Map, x = k.Value.X, y = k.Value.Y }));
    /// <summary>Load NPC roles (JSON from <see cref="ExportNpcs"/>).</summary>
    public void ImportNpcs(string json)
    {
        try
        {
            foreach (var e in System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray())
                _npc[e.GetProperty("id").GetInt32()] = (e.GetProperty("mask").GetInt32(), e.GetProperty("map").GetInt32(), e.GetProperty("x").GetUInt32(), e.GetProperty("y").GetUInt32());
        }
        catch (System.Text.Json.JsonException) { }
    }
}
