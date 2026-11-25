using System.Buffers;
using System.Text;
using System.Text.Json;
using Fiesta.Bot.Agent;

namespace Fiesta.Bot.Server;

/// <summary>One viewer of a bot's game stream.</summary>
public sealed class GameViewer(Func<string, bool> send)
{
    /// <summary>Send one message (false = the connection is gone).</summary>
    public Func<string, bool> Send { get; } = send;
    /// <summary>Messages sent to this viewer.</summary>
    public long Seq;
    /// <summary>Needs a snapshot before any delta.</summary>
    public bool NeedSnap = true;
    /// <summary>This viewer was told the bot is out of the world (not repeated).</summary>
    public bool ToldOff;
}

/// <summary>The `game` topic of one bot (API_SPEC 14.1): takes the tick's captures, keeps the state last sent, and
/// turns the difference into the op list. Runs on the hub's pump thread only; one instance per bot shared by all
/// viewers.</summary>
public sealed class GameStream(string bot, GameTap tap)
{
    private static readonly string?[] StatNames =
        ["str", "end", "dex", "int", null, "spr", "wcMin", "wcMax", "def", "aim", "eva", "maMin", "maMax", "mdef", "maim", "meva"];
    private const int PosIntervalMs = 250, PosMin = 4, KeepAliveMs = 5000;

    private sealed class Ent { public byte Kind; public int Id, X, Y; public byte Hp, Flags; public string? Name; }

    private readonly List<GameViewer> _viewers = new();
    private readonly ArrayBufferWriter<byte> _buf = new(4096);
    // the state the viewers have
    private bool _has;
    private string? _offReason;
    private int _session = -1, _mapId = int.MinValue, _target = -1, _x, _y, _bagSize = -1, _bagPages = -1;
    private readonly Dictionary<string, long> _self = new();
    private readonly long[] _stats = new long[16];
    private readonly Dictionary<int, long> _ab = new();
    private readonly Dictionary<int, (long Ready, int Dur)> _sk = new();
    private readonly int[] _bag = new int[256], _lot = new int[256], _eq = new int[64];
    private int[] _path = [];
    private readonly Dictionary<int, Ent> _ents = new();
    private readonly HashSet<int> _seen = new();
    private long _lastPosAt, _lastMsgAt, _lastNow;
    /// <summary>Bytes sent (all viewers).</summary>
    public long BytesSent;
    /// <summary>Messages sent (all viewers).</summary>
    public long MessagesSent;

    /// <summary>Bot id.</summary>
    public string Bot { get; } = bot;
    /// <summary>The tap this stream reads.</summary>
    public GameTap Tap { get; } = tap;
    /// <summary>Viewers.</summary>
    public int ViewerCount { get { lock (_viewers) return _viewers.Count; } }

    /// <summary>Add a viewer (any thread).</summary>
    public void Add(GameViewer v) { lock (_viewers) _viewers.Add(v); Interlocked.Increment(ref Tap.Viewers); }

    /// <summary>Remove a viewer (any thread).</summary>
    public void Remove(GameViewer v) { lock (_viewers) if (_viewers.Remove(v)) Interlocked.Decrement(ref Tap.Viewers); }

    /// <summary>One pump step (pump thread): read the newest capture, send snapshots / deltas / keep-alives.</summary>
    public void Pump(long nowUnixMs, string? offReason)
    {
        GameViewer[] viewers;
        lock (_viewers) { if (_viewers.Count == 0) return; viewers = _viewers.ToArray(); }
        var cap = Tap.TakeNewest();
        string? delta = null;
        if (offReason is not null)
        {
            // the bot is really out of the world (stopped, disconnected, reconnecting): say so once, with the reason
            if (_offReason != offReason) { _offReason = offReason; _has = false; Broadcast(viewers, "off", offReason, nowUnixMs); }
        }
        else if (cap is not null && cap.InWorld)
        {
            // a capture between two zone servers (map handoff) has InWorld false: skipped, the view just waits
            _lastNow = cap.Now;
            if (!_has || cap.Session != _session) { Load(cap); _has = true; _offReason = null; foreach (var v in viewers) v.NeedSnap = true; }
            else delta = Diff(cap);
        }

        foreach (var v in viewers)
        {
            if (v.NeedSnap)
            {
                if (_has) { v.NeedSnap = false; Send(v, "snap", Snapshot(), _lastNow); }
                else if (_offReason is not null && !v.ToldOff) { v.ToldOff = true; Send(v, "off", _offReason, nowUnixMs); }
                continue;   // in the world but nothing captured yet: the snapshot follows within 100 ms
            }
            if (delta is not null) Send(v, "d", delta, _lastNow);
        }
        if (delta is not null) _lastMsgAt = nowUnixMs;
        else if (_has && nowUnixMs - _lastMsgAt >= KeepAliveMs) { _lastMsgAt = nowUnixMs; Broadcast(viewers, "d", "[]", nowUnixMs); }
    }

    /// <summary>The host is shutting down: tell every viewer (any thread; the pump stops with the host).</summary>
    public void HostStopping(long nowUnixMs)
    {
        GameViewer[] viewers;
        lock (_viewers) viewers = _viewers.ToArray();
        foreach (var v in viewers) Send(v, "off", "host-stopping", nowUnixMs);
    }

    private void Broadcast(GameViewer[] viewers, string t, string? body, long now)
    {
        foreach (var v in viewers)
        {
            if (t == "off") { v.ToldOff = true; v.NeedSnap = true; Send(v, t, body, now); }
            else if (!v.NeedSnap) Send(v, t, body, now);
        }
    }

    private void Send(GameViewer v, string t, string? body, long now)
    {
        var sb = new StringBuilder(64 + (body?.Length ?? 0));
        sb.Append("{\"topic\":\"game\",\"bot\":").Append(JsonSerializer.Serialize(Bot)).Append(",\"t\":\"").Append(t)
          .Append("\",\"seq\":").Append(++v.Seq).Append(",\"now\":").Append(now);
        if (t == "off") sb.Append(",\"reason\":\"").Append(body ?? "left-world").Append('"');
        else if (body is not null) sb.Append(t == "snap" ? ",\"data\":" : ",\"ops\":").Append(body);
        sb.Append('}');
        var s = sb.ToString();
        if (v.Send(s)) { BytesSent += s.Length; MessagesSent++; }
    }

    private static readonly (string Name, bool Bool, Func<GameCapture, long> Get)[] SelfFields =
    [
        ("handle", false, c => c.Handle), ("classId", false, c => c.ClassId), ("level", false, c => c.Level), ("exp", false, c => c.Exp),
        ("prevExp", false, c => c.PrevExp), ("nextExp", false, c => c.NextExp), ("hp", false, c => c.Hp), ("maxHp", false, c => c.MaxHp),
        ("sp", false, c => c.Sp), ("maxSp", false, c => c.MaxSp), ("money", false, c => c.Money), ("fame", false, c => c.Fame),
        ("statPoints", false, c => c.StatPoints), ("hpStones", false, c => c.HpStones), ("spStones", false, c => c.SpStones),
        ("maxHpStones", false, c => c.MaxHpStones), ("maxSpStones", false, c => c.MaxSpStones), ("dead", true, c => c.Dead ? 1 : 0),
        ("battle", true, c => c.Battle ? 1 : 0), ("mounted", true, c => c.Mounted ? 1 : 0), ("dir", false, c => c.Dir), ("mapId", false, c => c.MapId),
    ];

    private string _name = "";

    // take a capture as the new sent state (a snapshot follows)
    private void Load(GameCapture c)
    {
        _session = c.Session; _mapId = c.MapId; _target = c.Target; _x = c.X; _y = c.Y; _name = c.Name;
        foreach (var f in SelfFields) _self[f.Name] = f.Get(c);
        Array.Copy(c.Stats, _stats, 16);
        _ab.Clear(); for (var i = 0; i < c.AbN; i++) _ab[c.AbId[i]] = c.AbEnd[i];
        _sk.Clear(); for (var i = 0; i < c.SkN; i++) _sk[c.SkId[i]] = (c.SkReady[i], c.SkDur[i]);
        _bagSize = c.BagSize; _bagPages = c.BagPages;
        Array.Copy(c.BagItem, _bag, 256); Array.Copy(c.BagLot, _lot, 256); Array.Copy(c.Equip, _eq, 64);
        _path = PathOf(c);
        _ents.Clear();
        for (var i = 0; i < c.EN; i++) _ents[c.EH[i]] = EntOf(c, i);
        _lastPosAt = c.Now;
    }

    private static Ent EntOf(GameCapture c, int i) => new() { Kind = c.EKind[i], Id = c.EId[i], X = c.EX[i], Y = c.EY[i], Hp = c.EHp[i], Flags = c.EFlags[i], Name = c.EName[i] };

    private static int[] PathOf(GameCapture c)
    {
        var p = new int[c.PathN * 2];
        for (var i = 0; i < c.PathN; i++) { p[2 * i] = c.PathX[i]; p[2 * i + 1] = c.PathY[i]; }
        return p;
    }

    private Utf8JsonWriter Writer() { _buf.Clear(); return new Utf8JsonWriter(_buf); }
    private string Done(Utf8JsonWriter w) { w.Flush(); w.Dispose(); return Encoding.UTF8.GetString(_buf.WrittenSpan); }

    private void WriteEnt(Utf8JsonWriter w, int h, Ent e)
    {
        w.WriteNumberValue(h); w.WriteStringValue(((char)e.Kind).ToString()); w.WriteNumberValue(e.Id);
        w.WriteNumberValue(e.X); w.WriteNumberValue(e.Y); w.WriteNumberValue(e.Hp); w.WriteNumberValue(e.Flags);
        if (e.Name is null) w.WriteNullValue(); else w.WriteStringValue(e.Name);
    }

    private string Snapshot()
    {
        var w = Writer();
        w.WriteStartObject();
        w.WriteStartObject("self");
        w.WriteString("name", _name);
        foreach (var f in SelfFields) { if (f.Bool) w.WriteBoolean(f.Name, _self[f.Name] != 0); else w.WriteNumber(f.Name, _self[f.Name]); }
        w.WriteNumber("x", _x); w.WriteNumber("y", _y);
        w.WriteEndObject();
        w.WriteStartObject("stats");
        for (var i = 0; i < 16; i++) if (StatNames[i] is { } n) w.WriteNumber(n, _stats[i]);
        w.WriteEndObject();
        w.WriteStartArray("abstates");
        foreach (var (id, end) in _ab) { w.WriteStartArray(); w.WriteNumberValue(id); w.WriteNumberValue(end); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteStartArray("skills");
        foreach (var (id, (r, d)) in _sk) { w.WriteStartArray(); w.WriteNumberValue(id); w.WriteNumberValue(r); w.WriteNumberValue(d); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteStartObject("bag");
        w.WriteNumber("size", _bagSize); w.WriteNumber("pages", _bagPages);
        w.WriteStartArray("cells");
        for (var i = 0; i < 256; i++) if (_bag[i] >= 0) { w.WriteStartArray(); w.WriteNumberValue(i); w.WriteNumberValue(_bag[i]); w.WriteNumberValue(_lot[i]); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteStartArray("equip");
        for (var i = 0; i < 64; i++) if (_eq[i] >= 0) { w.WriteStartArray(); w.WriteNumberValue(i); w.WriteNumberValue(_eq[i]); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteNumber("target", _target);
        WritePath(w, "path");
        w.WriteStartArray("ents");
        foreach (var (h, e) in _ents) { w.WriteStartArray(); WriteEnt(w, h, e); w.WriteEndArray(); }
        w.WriteEndArray();
        w.WriteEndObject();
        return Done(w);
    }

    private void WritePath(Utf8JsonWriter w, string? name)
    {
        if (name is null) w.WriteStartArray(); else w.WriteStartArray(name);
        for (var i = 0; i + 1 < _path.Length; i += 2) { w.WriteStartArray(); w.WriteNumberValue(_path[i]); w.WriteNumberValue(_path[i + 1]); w.WriteEndArray(); }
        w.WriteEndArray();
    }

    // the op list that brings the viewers from the sent state to this capture; null when nothing changed
    private string? Diff(GameCapture c)
    {
        var w = Writer();
        var n = 0;
        w.WriteStartArray();
        var posDue = c.Now - _lastPosAt >= PosIntervalMs;

        // map change: drop entities and path
        if (c.MapId != _mapId)
        {
            _mapId = c.MapId; _ents.Clear(); _path = [];
            w.WriteStartArray(); w.WriteStringValue("map"); w.WriteNumberValue(c.MapId); w.WriteEndArray(); n++;
            posDue = true;
        }

        // self
        var selfOpen = false;
        void SelfField(Utf8JsonWriter w2, string name)
        {
            if (!selfOpen) { w2.WriteStartArray(); w2.WriteStringValue("self"); w2.WriteStartObject(); selfOpen = true; n++; }
        }
        foreach (var f in SelfFields)
        {
            var v = f.Get(c);
            if (_self[f.Name] == v) continue;
            _self[f.Name] = v; SelfField(w, f.Name);
            if (f.Bool) w.WriteBoolean(f.Name, v != 0); else w.WriteNumber(f.Name, v);
        }
        if (c.Name != _name) { _name = c.Name; SelfField(w, "name"); w.WriteString("name", c.Name); }
        if (posDue && (Math.Abs(c.X - _x) >= PosMin || Math.Abs(c.Y - _y) >= PosMin))
        {
            _x = c.X; _y = c.Y; SelfField(w, "x"); w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y);
        }
        if (selfOpen) { w.WriteEndObject(); w.WriteEndArray(); }

        // stats
        var statsOpen = false;
        for (var i = 0; i < 16; i++)
        {
            if (StatNames[i] is not { } sn || _stats[i] == c.Stats[i]) continue;
            _stats[i] = c.Stats[i];
            if (!statsOpen) { w.WriteStartArray(); w.WriteStringValue("stats"); w.WriteStartObject(); statsOpen = true; n++; }
            w.WriteNumber(sn, c.Stats[i]);
        }
        if (statsOpen) { w.WriteEndObject(); w.WriteEndArray(); }

        // abstates
        _seen.Clear();
        for (var i = 0; i < c.AbN; i++)
        {
            var id = c.AbId[i]; _seen.Add(id);
            if (_ab.TryGetValue(id, out var end) && end == c.AbEnd[i]) continue;
            _ab[id] = c.AbEnd[i];
            w.WriteStartArray(); w.WriteStringValue("ab"); w.WriteNumberValue(id); w.WriteNumberValue(c.AbEnd[i]); w.WriteEndArray(); n++;
        }
        foreach (var id in _ab.Keys.Where(k => !_seen.Contains(k)).ToList())
        {
            _ab.Remove(id);
            w.WriteStartArray(); w.WriteStringValue("ab-"); w.WriteNumberValue(id); w.WriteEndArray(); n++;
        }

        // skills
        _seen.Clear();
        for (var i = 0; i < c.SkN; i++)
        {
            var id = c.SkId[i]; _seen.Add(id);
            if (_sk.TryGetValue(id, out var cur) && cur.Ready == c.SkReady[i]) continue;
            _sk[id] = (c.SkReady[i], c.SkDur[i]);
            w.WriteStartArray(); w.WriteStringValue("sk"); w.WriteNumberValue(id); w.WriteNumberValue(c.SkReady[i]); w.WriteNumberValue(c.SkDur[i]); w.WriteEndArray(); n++;
        }
        foreach (var id in _sk.Keys.Where(k => !_seen.Contains(k)).ToList())
        {
            _sk.Remove(id);
            w.WriteStartArray(); w.WriteStringValue("sk-"); w.WriteNumberValue(id); w.WriteEndArray(); n++;
        }

        // bag / equipment
        if (c.BagSize != _bagSize || c.BagPages != _bagPages)
        {
            _bagSize = c.BagSize; _bagPages = c.BagPages;
            w.WriteStartArray(); w.WriteStringValue("bagSize"); w.WriteNumberValue(c.BagSize); w.WriteNumberValue(c.BagPages); w.WriteEndArray(); n++;
        }
        for (var i = 0; i < 256; i++)
        {
            if (_bag[i] == c.BagItem[i] && _lot[i] == c.BagLot[i]) continue;
            _bag[i] = c.BagItem[i]; _lot[i] = c.BagLot[i];
            w.WriteStartArray(); w.WriteStringValue("bag"); w.WriteNumberValue(i); w.WriteNumberValue(c.BagItem[i]); w.WriteNumberValue(c.BagLot[i]); w.WriteEndArray(); n++;
        }
        for (var i = 0; i < 64; i++)
        {
            if (_eq[i] == c.Equip[i]) continue;
            _eq[i] = c.Equip[i];
            w.WriteStartArray(); w.WriteStringValue("eq"); w.WriteNumberValue(i); w.WriteNumberValue(c.Equip[i]); w.WriteEndArray(); n++;
        }

        // target, path
        if (c.Target != _target)
        {
            _target = c.Target;
            w.WriteStartArray(); w.WriteStringValue("tgt"); w.WriteNumberValue(c.Target); w.WriteEndArray(); n++;
        }
        var samePath = _path.Length == c.PathN * 2;
        for (var i = 0; samePath && i < c.PathN; i++) samePath = _path[2 * i] == c.PathX[i] && _path[2 * i + 1] == c.PathY[i];
        if (!samePath)
        {
            _path = PathOf(c);
            w.WriteStartArray(); w.WriteStringValue("path"); WritePath(w, null); w.WriteEndArray(); n++;
        }

        // entities
        _seen.Clear();
        for (var i = 0; i < c.EN; i++)
        {
            var h = c.EH[i]; _seen.Add(h);
            if (!_ents.TryGetValue(h, out var e) || e.Kind != c.EKind[i] || e.Id != c.EId[i])
            {
                e = EntOf(c, i); _ents[h] = e;
                w.WriteStartArray(); w.WriteStringValue("e+"); WriteEnt(w, h, e); w.WriteEndArray(); n++;
                continue;
            }
            if (posDue && (Math.Abs(e.X - c.EX[i]) >= PosMin || Math.Abs(e.Y - c.EY[i]) >= PosMin))
            {
                e.X = c.EX[i]; e.Y = c.EY[i];
                w.WriteStartArray(); w.WriteStringValue("ep"); w.WriteNumberValue(h); w.WriteNumberValue(e.X); w.WriteNumberValue(e.Y); w.WriteEndArray(); n++;
            }
            if (e.Hp != c.EHp[i])
            {
                e.Hp = c.EHp[i];
                w.WriteStartArray(); w.WriteStringValue("eh"); w.WriteNumberValue(h); w.WriteNumberValue(e.Hp); w.WriteEndArray(); n++;
            }
            if (e.Flags != c.EFlags[i])
            {
                e.Flags = c.EFlags[i];
                w.WriteStartArray(); w.WriteStringValue("ef"); w.WriteNumberValue(h); w.WriteNumberValue(e.Flags); w.WriteEndArray(); n++;
            }
        }
        if (_ents.Count != _seen.Count)
            foreach (var h in _ents.Keys.Where(k => !_seen.Contains(k)).ToList())
            {
                _ents.Remove(h);
                w.WriteStartArray(); w.WriteStringValue("e-"); w.WriteNumberValue(h); w.WriteEndArray(); n++;
            }
        if (posDue) _lastPosAt = c.Now;

        w.WriteEndArray();
        var s = Done(w);
        return n == 0 ? null : s;
    }
}
