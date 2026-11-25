using System.Threading;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Agent;

/// <summary>One capture of what the game client would show for this bot (API_SPEC 14). Filled on the tick thread into
/// preallocated arrays (no allocation); read on the host's pump thread. Times are unix milliseconds.</summary>
public sealed class GameCapture
{
    /// <summary>Capture sequence (increases per fill).</summary>
    public long Seq;
    /// <summary>Unix ms of the capture.</summary>
    public long Now;
    /// <summary>In the world.</summary>
    public bool InWorld;
    /// <summary>Session counter (a new session = a new snapshot).</summary>
    public int Session;
    /// <summary>Self fields.</summary>
    public int Handle, ClassId, Level, MapId, X, Y, Dir;
    /// <summary>Self name.</summary>
    public string Name = "";
    /// <summary>Self numbers (-1 = not received yet).</summary>
    public long Exp, PrevExp, NextExp, Money, Fame, StatPoints, HpStones, SpStones, MaxHpStones, MaxSpStones, Hp, MaxHp, Sp, MaxSp;
    /// <summary>Self flags.</summary>
    public bool Dead, Battle, Mounted;
    /// <summary>Stats 0..15 (-1 unknown).</summary>
    public readonly long[] Stats = new long[16];
    /// <summary>Abstate wire ids.</summary>
    public readonly int[] AbId = new int[64];
    /// <summary>Abstate ends (unix ms, 0 = no end).</summary>
    public readonly long[] AbEnd = new long[64];
    /// <summary>Abstate count.</summary>
    public int AbN;
    /// <summary>Skill ids.</summary>
    public readonly int[] SkId = new int[512];
    /// <summary>Skill ready times (unix ms, 0 = ready).</summary>
    public readonly long[] SkReady = new long[512];
    /// <summary>Skill cooldown lengths (ms) of the running cooldown.</summary>
    public readonly int[] SkDur = new int[512];
    /// <summary>Skill count.</summary>
    public int SkN;
    /// <summary>Bag size and pages.</summary>
    public int BagSize, BagPages;
    /// <summary>Bag cells (-1 empty).</summary>
    public readonly int[] BagItem = new int[256], BagLot = new int[256];
    /// <summary>Equipment (-1 empty).</summary>
    public readonly int[] Equip = new int[64];
    /// <summary>Target handle (-1 none).</summary>
    public int Target;
    /// <summary>Remaining path.</summary>
    public readonly int[] PathX = new int[64], PathY = new int[64];
    /// <summary>Path length.</summary>
    public int PathN;
    /// <summary>Entity handles, ids, positions.</summary>
    public readonly int[] EH = new int[EntityTable.Cap], EId = new int[EntityTable.Cap], EX = new int[EntityTable.Cap], EY = new int[EntityTable.Cap];
    /// <summary>Entity kind letter, HP percentage, flags (1 dead, 2 aggro, 4 our target, 8 huntable).</summary>
    public readonly byte[] EKind = new byte[EntityTable.Cap], EHp = new byte[EntityTable.Cap], EFlags = new byte[EntityTable.Cap];
    /// <summary>Entity names (players, gates).</summary>
    public readonly string?[] EName = new string?[EntityTable.Cap];
    /// <summary>Entity count.</summary>
    public int EN;

    /// <summary>Fill from the world (tick thread).</summary>
    [HotPath]
    public void Fill(AgentContext c, int session)
    {
        var w = c.World; var self = w.Self; var now = c.Now;
        Now = SystemClock.UnixMs(now); Session = session; InWorld = self.InWorld && w.LinkUp;
        Handle = self.Handle; ClassId = self.ClassId; Level = self.Level; MapId = w.Map.MapId; Name = self.Name;
        X = (int)Math.Round(self.Pos.X); Y = (int)Math.Round(self.Pos.Y);
        Dir = (int)Math.Round(Math.Atan2(self.Facing.Dy, self.Facing.Dx) * 180 / Math.PI);
        Exp = self.Exp; PrevExp = self.PrevExp; NextExp = self.NextExp; Money = self.MoneyKnown ? self.Money : -1;
        Fame = self.Fame; StatPoints = self.StatPoints; HpStones = self.HpStones; SpStones = self.SpStones;
        (MaxHpStones, MaxSpStones) = self.MaxStones;
        Hp = (long)self.Hp; MaxHp = (long)self.MaxHp; Sp = (long)self.Sp; MaxSp = (long)self.MaxSp;
        Dead = self.Dead; Battle = self.BattleMode; Mounted = self.Mounted;
        for (var i = 0; i < 16; i++) Stats[i] = (long)self.Stat(i);

        AbN = 0;
        for (var i = 0; i < self.AbstateCount && AbN < AbId.Length; i++)
        {
            var (id, until) = self.AbstateAt(i);
            if (until != long.MaxValue && until <= now) continue;
            AbId[AbN] = id; AbEnd[AbN] = until == long.MaxValue ? 0 : SystemClock.UnixMs(until); AbN++;
        }

        SkN = 0;
        var learned = w.Skills.Learned;
        for (var i = 0; i < learned.Count && SkN < SkId.Length; i++)
        {
            var id = learned[i];
            var row = c.Data.Skill(id);
            var ready = w.Skills.ReadyAt(id); var dur = row?.DelayMs ?? 0;
            var g = row is null ? 0 : w.Skills.GroupReadyAt(row.DelayGroup);
            if (g > ready) { ready = g; dur = row!.DelayGroupMs; }
            SkId[SkN] = id; SkReady[SkN] = ready > now ? SystemClock.UnixMs(ready) : 0; SkDur[SkN] = ready > now ? dur : 0; SkN++;
        }

        BagSize = w.Bag.Size; BagPages = w.Bag.Pages;
        for (var i = 0; i < 256; i++) { var (it, lot) = w.Bag.At(i); BagItem[i] = it; BagLot[i] = lot; }
        for (var i = 0; i < 64; i++) Equip[i] = w.Bag.Equipped(i);

        Target = c.Combat.Target;
        PathN = 0;
        var path = c.Move.Path;
        if (c.Move.Moving)
            for (var i = Math.Max(0, c.Move.Leg); i < path.Count && PathN < PathX.Length; i++) { PathX[PathN] = (int)path[i].X; PathY[PathN] = (int)path[i].Y; PathN++; }

        EN = 0;
        var t = w.Entities;
        for (var i = 0; i < t.Count; i++)
        {
            var s = t.LiveSlot(i);
            var k = w.KindOf(s, now);
            var (x, y) = t.Pos(s, now);
            EH[EN] = t.HandleOf(s);
            EKind[EN] = (k & EntityKind.Gate) != 0 ? (byte)'g' : (k & EntityKind.Drop) != 0 ? (byte)'d' : (k & EntityKind.Player) != 0 ? (byte)'p'
                : (k & EntityKind.Npc) != 0 ? (byte)'n' : (byte)'m';
            EId[EN] = t.MobId(s);
            EX[EN] = (int)Math.Round(x); EY[EN] = (int)Math.Round(y);
            var mh = t.MaxHp(s);
            EHp[EN] = (byte)(mh > 0 ? Math.Clamp((int)Math.Round(t.Hp(s) * 100 / mh), 0, 100) : 100);
            EFlags[EN] = (byte)((t.IsDead(s) ? 1 : 0) | ((k & EntityKind.Aggro) != 0 ? 2 : 0) | (EH[EN] == Target ? 4 : 0) | ((k & EntityKind.Huntable) != 0 ? 8 : 0));
            EName[EN] = (k & (EntityKind.Player | EntityKind.Gate)) != 0 ? t.NameOf(s) : null;
            EN++;
        }
    }
}

/// <summary>Hands game captures from the tick to the host's pump: a triple buffer (the tick fills its own buffer and
/// swaps it into the middle; the reader swaps the middle out). No locks, no allocation on the tick.</summary>
public sealed class GameTap
{
    private GameCapture _write = new(), _middle = new(), _read = new();
    private long _seq, _lastCapture = long.MinValue / 2, _lastRead;
    /// <summary>Number of viewers (the tick captures only while it is above zero).</summary>
    public int Viewers;
    /// <summary>Capture interval (ms).</summary>
    public int IntervalMs { get; set; } = 100;
    /// <summary>Session counter, bumped by the supervisor on each new session.</summary>
    public int Session;

    /// <summary>Tick side: capture when someone watches and the interval passed.</summary>
    [HotPath]
    public void MaybeCapture(AgentContext c)
    {
        if (Volatile.Read(ref Viewers) <= 0 || c.Now - _lastCapture < IntervalMs) return;
        _lastCapture = c.Now;
        _write.Fill(c, Volatile.Read(ref Session));
        _write.Seq = ++_seq;
        _write = Interlocked.Exchange(ref _middle, _write);
    }

    /// <summary>Reader side (one reader thread): the newest capture since the last call, or null. The returned
    /// buffer stays the reader's until its next call.</summary>
    public GameCapture? TakeNewest()
    {
        _read = Interlocked.Exchange(ref _middle, _read);
        if (_read.Seq <= _lastRead) return null;
        _lastRead = _read.Seq;
        return _read;
    }
}
