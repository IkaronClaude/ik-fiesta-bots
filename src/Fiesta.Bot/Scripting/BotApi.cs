using Fiesta.Bot.Manager;
using Fiesta.Bot.Pathfinding;
using MoonSharp.Interpreter;

namespace Fiesta.Bot.Scripting;

/// <summary>
/// The Lua-facing facade for ONE bot — the <c>bot</c> global a behaviour script
/// drives. Every action method forwards to <see cref="BotManager"/> (the same seam
/// the HTTP endpoints use); every getter reads the bot's live <see cref="Session.ZoneView"/>
/// / <see cref="BotHandle"/> perception. Actions are synchronous from Lua's view: the
/// underlying sends are quick and the runner gives each bot its own dedicated script
/// thread, so blocking on them is fine and keeps scripts deterministic.
///
/// <para>Method names are intentionally camelCase (not PascalCase) so scripts read
/// idiomatically — <c>bot.cast(1500, h)</c>, <c>bot.hp()</c>. Registered as a MoonSharp
/// userdata; table-returning getters build Lua tables via the attached <see cref="Script"/>
/// (always called on the script thread, so the non-thread-safe VM is never touched
/// concurrently).</para>
/// </summary>
[MoonSharpUserData]
public sealed class BotApi
{
    private readonly BotManager _mgr;
    private readonly BotHandle _handle;
    private Script? _lua;

    internal BotApi(BotManager mgr, BotHandle handle)
    {
        _mgr = mgr;
        _handle = handle;
    }

    /// <summary>Attach the owning VM (so getters can build Lua tables). Called once by
    /// the runner on the script thread before any script code runs.</summary>
    internal void AttachScript(Script lua) => _lua = lua;

    private string Id => _handle.Id;
    private Session.ZoneView? View => _handle.ZoneView;
    private static bool Ok(BotManager.ActionResult r) => r == BotManager.ActionResult.Sent;
    private static T Wait<T>(Task<T> t) => t.GetAwaiter().GetResult();

    // ── actions (C→S) ─────────────────────────────────────────────────────────
    public bool say(string text) => Ok(Wait(_mgr.SayAsync(Id, text)));
    public bool whisper(string to, string text) => Ok(Wait(_mgr.WhisperAsync(Id, to, text)));
    public bool cast(int skill, int target) => Ok(Wait(_mgr.CastAsync(Id, (ushort)skill, (ushort)target)));
    public bool castGround(int skill, double x, double y) => Ok(Wait(_mgr.CastGroundAsync(Id, (ushort)skill, (uint)x, (uint)y)));
    public bool attack(int skill, int target = 0) => Ok(Wait(_mgr.AttackAsync(Id, (ushort)skill, (ushort)target)));
    public bool autoAttack(int target = 0) => Ok(Wait(_mgr.AutoAttackAsync(Id, (ushort)target)));
    public bool stopAttack() => Ok(Wait(_mgr.StopAttackAsync(Id)));
    public bool heal(int skill) => Ok(Wait(_mgr.HealSelfAsync(Id, (ushort)skill)));
    public bool useItem(int slot, int invenType = 9) => Ok(Wait(_mgr.UseItemAsync(Id, (byte)slot, (byte)invenType)));
    public bool equip(int slot) => Ok(Wait(_mgr.EquipAsync(Id, (byte)slot)));
    public bool soulstoneHp() => Ok(Wait(_mgr.UseSoulStoneHpAsync(Id)));
    public bool soulstoneSp() => Ok(Wait(_mgr.UseSoulStoneSpAsync(Id)));
    public bool target(int handle) => Ok(Wait(_mgr.TargetAsync(Id, (ushort)handle)));
    public bool untarget() => Ok(Wait(_mgr.UntargetAsync(Id)));
    public bool walk(double fx, double fy, double tx, double ty) => Ok(Wait(_mgr.WalkAsync(Id, (uint)fx, (uint)fy, (uint)tx, (uint)ty)));
    public bool travelTo(string map) => _mgr.TravelTo(Id, map).Result == BotManager.TravelResult.Started;
    public bool stopTravel() => Ok(_mgr.StopTravel(Id));
    public bool follow(string name) => Ok(_mgr.Follow(Id, name));
    public bool stopFollow() => Ok(_mgr.StopFollow(Id));
    public bool useGate(int handle) => Ok(Wait(_mgr.UseGateAsync(Id, (ushort)handle)));
    public bool townPortal(int npcHandle, int dest) => Ok(Wait(_mgr.TownPortalAsync(Id, (ushort)npcHandle, (byte)dest)));

    /// <summary>Issue a GM command (prepends '&amp;' if no prefix), e.g. <c>bot.gm("levelup 1")</c>.</summary>
    public bool gm(string command)
    {
        var c = command.Trim();
        if (c.Length > 0 && c[0] != '&' && c[0] != '$') c = "&" + c;
        return Ok(Wait(_mgr.GmAsync(Id, c)));
    }

    /// <summary>Pathfind over the current map's block grid and walk to (x,y). Returns
    /// false if no grid / position / path. Uses the bot's tracked position as the start
    /// and its current map — same machinery as the <c>/walkto</c> endpoint.</summary>
    public bool walkTo(double x, double y)
    {
        if (_handle.CurrentMap is not { } map) return false;
        if (_mgr.GridProvider?.Invoke(map) is not { } grid) return false;
        if (_handle.Position is not { } pos) return false;
        var path = PathFinder.FindPath(grid, pos.X, pos.Y, (uint)x, (uint)y);
        if (path.Count == 0) return false;
        return Ok(_mgr.WalkPath(Id, PathFinder.Simplify(path)));
    }

    public bool partyInvite(string name) => Ok(Wait(_mgr.PartyInviteAsync(Id, name)));
    public bool partyAccept(string name) => Ok(Wait(_mgr.PartyAcceptAsync(Id, name)));
    public bool partyDecline(string name) => Ok(Wait(_mgr.PartyDeclineAsync(Id, name)));
    public bool partyChat(string text) => Ok(Wait(_mgr.PartyChatAsync(Id, text)));
    public bool friendAdd(string name) => Ok(Wait(_mgr.FriendAddAsync(Id, name)));
    public bool friendConfirm(string name, bool accept) => Ok(Wait(_mgr.FriendConfirmAsync(Id, name, accept)));
    public bool friendDelete(string name) => Ok(Wait(_mgr.FriendDeleteAsync(Id, name)));

    // ── state / vitals ──────────────────────────────────────────────────────────
    public double? hp() => View?.Hp;
    public double? sp() => View?.Sp;
    public double maxHp() => View?.MaxHp ?? 0;
    public double maxSp() => View?.MaxSp ?? 0;

    /// <summary>Current HP as a 0–100 percentage of max, or -1 if HP/max isn't known yet.
    /// The usual "heal / HP-stone when low" gate: <c>if bot.hpPct() &lt; 40 then ...</c>.</summary>
    public double hpPct()
    {
        var v = View;
        if (v is null || v.MaxHp == 0 || v.Hp is not { } h) return -1;
        return 100.0 * h / v.MaxHp;
    }

    public double spPct()
    {
        var v = View;
        if (v is null || v.MaxSp == 0 || v.Sp is not { } s) return -1;
        return 100.0 * s / v.MaxSp;
    }

    public double? x() => _handle.Position?.X;
    public double? y() => _handle.Position?.Y;
    public string? map() => _handle.CurrentMap;
    public int? selfHandle() => _handle.SelfHandle;
    public bool mounted() => View?.IsMounted ?? false;
    public double walkSpeed() => _handle.WalkSpeed;
    public string phase() => _handle.Phase.ToString();
    public bool inZone() => _handle.Phase == BotPhase.InZone;

    /// <summary>A monotonic millisecond clock for script-side cooldowns
    /// (<c>if bot.now() - last > 3000 then ...</c>).</summary>
    public double now() => Environment.TickCount64;

    public void log(string message) => _handle.Log($"[lua] {message}");

    // ── perception (tables) ───────────────────────────────────────────────────
    /// <summary>Zone handle of the nearest non-gate mob/NPC in view, or nil.</summary>
    public DynValue nearestMob()
    {
        var v = View; var pos = _handle.Position;
        if (v is null || pos is not { } p) return DynValue.Nil;
        int? best = null; var bestD = double.MaxValue;
        foreach (var n in v.NearbyNpcs)
        {
            if (n.IsGate) continue;
            var d = Sq((double)n.X - p.X) + Sq((double)n.Y - p.Y);
            if (d < bestD) { bestD = d; best = n.Handle; }
        }
        return best is { } b ? DynValue.NewNumber(b) : DynValue.Nil;
    }

    public DynValue nearbyMobs()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var pos = _handle.Position;
        var i = 1;
        foreach (var n in v.NearbyNpcs)
        {
            var row = NewTable();
            row["handle"] = n.Handle; row["mobId"] = n.MobId; row["mode"] = n.Mode;
            row["x"] = n.X; row["y"] = n.Y; row["isGate"] = n.IsGate; row["linkMap"] = n.LinkMap;
            if (pos is { } p) row["dist"] = Math.Sqrt(Sq((double)n.X - p.X) + Sq((double)n.Y - p.Y));
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    public DynValue nearbyPlayers()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var i = 1;
        foreach (var p in v.NearbyPlayers)
            t[i++] = PlayerRow(p);
        return DynValue.NewTable(t);
    }

    public DynValue gates()
    {
        var t = NewTable();
        var v = View; if (v is null) return DynValue.NewTable(t);
        var i = 1;
        foreach (var n in v.NearbyNpcs)
        {
            if (!n.IsGate) continue;
            var row = NewTable();
            row["handle"] = n.Handle; row["x"] = n.X; row["y"] = n.Y; row["linkMap"] = n.LinkMap;
            t[i++] = row;
        }
        return DynValue.NewTable(t);
    }

    public DynValue inventory()
    {
        var t = NewTable();
        var inv = View?.Inventory; if (inv is null) return DynValue.NewTable(t);
        foreach (var (slot, itemId) in inv) t[(int)slot] = itemId;
        return DynValue.NewTable(t);
    }

    public DynValue equipment()
    {
        var t = NewTable();
        var eq = View?.Equipment; if (eq is null) return DynValue.NewTable(t);
        foreach (var (slot, itemId) in eq) t[(int)slot] = itemId;
        return DynValue.NewTable(t);
    }

    /// <summary>Resolve a nearby player by name (case-insensitive) to a row table, or nil.</summary>
    public DynValue playerByName(string name)
    {
        var p = View?.NearbyPlayers.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return p is null ? DynValue.Nil : PlayerRow(p);
    }

    private DynValue PlayerRow(Session.NearbyPlayer p)
    {
        var row = NewTable();
        row["handle"] = p.Handle; row["name"] = p.Name; row["class"] = p.Class;
        row["level"] = p.Level; row["x"] = p.X; row["y"] = p.Y;
        return DynValue.NewTable(row);
    }

    private Table NewTable() => new(_lua);
    private static double Sq(double a) => a * a;
}
