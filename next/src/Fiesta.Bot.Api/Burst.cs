using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Api;

/// <summary>"Can I burst this before I die?" - the decision a player makes before committing to a fight, for any class
/// and any mob, from what the bot knows now:
///   * time to kill: our next actions simulated in order of soonest availability - each damage skill when its cooldown
///     (and group cooldown) is up, the SP is there and the previous cast has finished (cast time / the measured spacing
///     between our casts), plus auto-attack swings for a melee fighter - until the damage reaches the mob's HP (wire);
///   * time to die: our HP (and a soul stone if one is ready) against the incoming damage of everything on us and the
///     target, per attacker from its measured hit and attack interval;
///   * both on the pessimistic side of their measured spread (our damage minus <see cref="ConfidenceSd"/> sd, their hits
///     at the highest seen), so the margin comes from the spread, not a constant. Commit when time to kill is shorter.
/// Damage per skill is measured (CombatStats); a skill never measured takes its data estimate scaled by how the measured
/// skills compare with theirs.</summary>
public static class Burst
{
    /// <summary>How many standard deviations of our damage the plan gives away (bot behaviour: one sd is the pessimistic
    /// side of ~84 % of hits; listed for review).</summary>
    public const double ConfidenceSd = 1.0;
    /// <summary>Planned actions at most (bot behaviour: a cap on the simulation, far above any real fight's casts).</summary>
    public const int MaxSteps = 256;

    /// <summary>One decision.</summary>
    public readonly record struct Plan(bool Commit, double TtkS, double TtdS, int FirstSkill, int Steps, string Why, int UnknownAttackers);

    /// <summary>Decide for target h. <paramref name="swings"/>: this character auto-attacks between casts (melee).</summary>
    public static Plan Decide(AgentContext ctx, int h, bool swings, System.Text.StringBuilder? sequence = null)
    {
        var w = ctx.World; var d = ctx.Data; var f = w.Fight;
        var slot = w.Entities.SlotOf(h);
        if (slot < 0 || w.Entities.IsDead(slot)) return new(false, double.PositiveInfinity, double.PositiveInfinity, -1, 0, "no target", 0);
        double mobHp = w.Entities.Hp(slot);
        // the walk in, as the game client does it: an action out of its reach (skillable / attackable + slack) starts
        // after walking to that distance less one, at our run speed
        var (tx, ty) = w.Entities.Pos(slot, ctx.Now);
        var dist = MoveController.Dist(w.Self.Pos.X, w.Self.Pos.Y, tx, ty);
        var speed = Math.Max(1, w.Self.RunSpeed);
        double Approach(double reachTo, double slack) => dist < reachTo + slack ? 0 : Math.Max(0, dist - (reachTo - 1)) / speed;
        if (mobHp <= 0) mobHp = d.Mob(w.Entities.MobId(slot))?.MaxHp ?? 0;

        // the prior scale for skills never measured: measured mean over data estimate, averaged over the measured skills
        double ratio = 0, sdFrac = 0; var rn = 0;
        foreach (var m in f.MeasuredSkills)
        {
            var est = Estimate(d, m); var (mean, sd, n) = f.Skill(m);
            if (est > 0 && n > 0) { ratio += mean / est; sdFrac += mean > 0 ? sd / mean : 0; rn++; }
        }
        if (rn > 0) { ratio /= rn; sdFrac /= rn; } else ratio = 1;

        // the equipped weapon (client data): the swing's prior before swings are measured, and the base a physical skill adds
        // to - a fighter's skills carry little damage of their own, they scale the weapon (without this a fresh fighter
        // saw "no damage source", pulled nothing, and so never measured anything: NextA hunted 30 min without one fight)
        var weapon = d.Item(w.Bag.Equipped(WeaponSlot(d, w)));
        var weaponDmg = weapon is null ? 0 : (weapon.MinWc + weapon.MaxWc) / 2.0;
        var weaponIv = weapon is { AtkSpeed: > 0 } ? weapon.AtkSpeed : 0;

        // the cast queue
        Span<int> ids = stackalloc int[32]; Span<double> ready = stackalloc double[32]; Span<double> dmg = stackalloc double[32];
        var k = 0;
        foreach (var id in w.Skills.Learned)
        {
            if (k >= ids.Length) break;
            if ((SkillTags.Of(d, id) & (int)SkillTag.Damage) == 0 || d.Skill(id) is not { } sk) continue;
            // a skill our weapon cannot use is no part of the plan (Slice and Dice with an axe opened every plan and was
            // refused, NextA 17:27)
            if (!GameRules.WeaponMeets(ctx, sk.DemandType)) continue;
            var (mean, sd, n) = f.Skill(id);
            if (n == 0)
            {
                var est = Estimate(d, id) + (sk.MinMa + sk.MaxMa > 0 ? 0 : weaponDmg);   // a physical skill hits with the weapon
                mean = est * ratio; sd = mean * sdFrac;
            }
            var low = Math.Max(0, mean - ConfidenceSd * sd);
            if (low <= 0) continue;
            ids[k] = id; dmg[k] = low;
            // ready: its cooldown, the walk into its reach, and a target that spawned moments ago (skills wait out its guard)
            ready[k] = Math.Max(Math.Max(w.Skills.ReadyInMs(id, sk.DelayGroup, ctx.Now), GameRules.SpawnGuardLeftMs(ctx, slot)) / 1000.0,
                Approach(GameRules.Skillable(ctx, sk, slot), GameRules.ClientSlack));
            k++;
        }
        // swings: measured, else the weapon's data (its damage at its attack interval)
        double swingDmg = 0, swingIv = 0;
        if (swings && f.Swing.N > 0 && f.SwingInterval.N > 0)
        { swingDmg = Math.Max(0, f.Swing.Mean - ConfidenceSd * f.Swing.Sd); swingIv = (f.SwingInterval.Mean + ConfidenceSd * f.SwingInterval.Sd) / 1000.0; }
        else if (swings && weaponDmg > 0 && weaponIv > 0) { swingDmg = weaponDmg; swingIv = weaponIv / 1000.0; }
        var nextSwing = swingDmg > 0
            ? Math.Max(Math.Max(0, swingIv - Math.Max(0, ctx.Now - ctx.Combat.LastSwingAt) / 1000.0), Approach(GameRules.Attackable(ctx, slot), GameRules.SwingStartSlack))
            : double.PositiveInfinity;
        var spacing = f.CastSpacing > 0 ? f.CastSpacing / 1000.0 : 0;
        var castFree = ctx.Combat.Casting(ctx.Now) ? spacing : 0.0;   // a cast in flight holds the next one by the spacing

        // time to die first: it bounds the simulation
        var (ttd, unknown) = TimeToDie(ctx, slot);

        double t = 0, left = mobHp, sp = w.Self.Sp; var steps = 0; var first = -1; var killedAt = double.PositiveInfinity;
        while (left > 0 && steps < MaxSteps)
        {
            // the soonest action: a skill (ready, SP, the previous cast finished) or a swing
            var best = -1; var bestAt = double.PositiveInfinity;
            for (var i = 0; i < k; i++)
            {
                if (d.Skill(ids[i]) is not { } sk || sk.Sp > sp) continue;
                var at = Math.Max(ready[i], castFree);
                // the soonest; at the same moment the harder hitter (ranks of one skill share a group: the higher rank)
                if (at < bestAt || (at == bestAt && best >= 0 && dmg[i] > dmg[best])) { bestAt = at; best = i; }
            }
            if (best < 0 && double.IsPositiveInfinity(nextSwing)) break;
            if (nextSwing <= bestAt)
            {
                t = nextSwing; left -= swingDmg; nextSwing += swingIv; steps++;
                sequence?.Append(" swing@").Append(t.ToString("0.0"));
            }
            else
            {
                var sk = d.Skill(ids[best])!;
                t = bestAt + sk.CastTimeMs / 1000.0;   // the damage lands when the cast completes
                left -= dmg[best]; sp -= sk.Sp; steps++;
                ready[best] = bestAt + Math.Max(sk.DelayMs, sk.DelayGroupMs) / 1000.0;
                // the group's cooldown holds every skill of the group (the ranks of a skill share one)
                if (sk.DelayGroup > 0)
                    for (var j = 0; j < k; j++)
                        if (j != best && d.Skill(ids[j]) is { } sj && sj.DelayGroup == sk.DelayGroup)
                            ready[j] = Math.Max(ready[j], bestAt + sk.DelayGroupMs / 1000.0);
                castFree = bestAt + Math.Max(sk.CastTimeMs / 1000.0, spacing);
                if (first < 0) first = ids[best];
                sequence?.Append(' ').Append(sk.Name).Append('@').Append(t.ToString("0.0"));
            }
            if (left <= 0) killedAt = t;
            if (t > ttd) break;   // beyond our death: the answer is already no
        }
        var commit = killedAt < ttd;
        var why = commit ? "we kill first"
            : k == 0 && swingDmg <= 0 ? "no damage source"
            : left > 0 && t <= ttd ? "runs out of SP / actions"
            : "it kills us first";
        return new(commit, killedAt, ttd, first, steps, why, unknown);
    }

    // the equipment slot weapons go in, from the client data of what we wear (the slot holding an item with weapon damage)
    private static int WeaponSlot(IClientData d, WorldState w)
    {
        for (var slot = 0; slot < 64; slot++)
            if (w.Bag.Equipped(slot) is var it and >= 0 && d.Item(it) is { } i && i.MaxWc > 0 && i.WeaponType > 0) return slot;
        return -1;
    }

    // a skill's own damage from data (the prior before it is measured)
    private static double Estimate(IClientData d, int id) => d.Skill(id) is { } s ? (s.MinWc + s.MaxWc + s.MinMa + s.MaxMa) / 2.0 : 0;

    /// <summary>Seconds until our HP is gone under everything on us and the target, at their highest seen hits and their
    /// measured attack intervals, a ready soul stone included; attackers with no measured hit are counted as unknown.</summary>
    public static (double Ttd, int Unknown) TimeToDie(AgentContext ctx, int targetSlot)
    {
        var w = ctx.World; var f = w.Fight;
        double dps = 0; var unknown = 0;
        var s = ctx.Scratch(1024); var (x, y) = w.Self.Pos;
        var n = w.Grid.Query(x, y, 2500, EntityKind.Mob | EntityKind.Alive, w, ctx.Now, s);
        for (var i = 0; i <= n; i++)
        {
            var sl = i < n ? s[i] : targetSlot;
            if (i < n && sl == targetSlot) continue;
            if (sl < 0) continue;
            // on us: the target, anything that has us as its target, or hit us within its last few intervals
            var iv = ctx.Knowledge.Mob(w.Entities.MobId(sl), MobFact.AttackInterval);
            var recent = iv.Samples > 0 ? 3 * iv.Value : 0;
            var onUs = sl == targetSlot || w.Entities.TargetOf(sl) == w.Self.Handle
                || (w.Entities.LastHitUsAt(sl) >= 0 && ctx.Now - w.Entities.LastHitUsAt(sl) <= recent);
            if (!onUs) continue;
            var hit = ctx.Knowledge.Mob(w.Entities.MobId(sl), MobFact.HitAvg);
            if (hit.Samples == 0 || iv.Samples == 0) { unknown++; continue; }
            var worst = hit.Samples > 1 ? Math.Max(hit.Value, hit.High) : hit.Value;
            dps += worst / Math.Max(1e-3, iv.Value / 1000.0);
        }
        double hp = w.Self.Hp;
        var stoneReady = w.Self.HpStones > 0 && f.StoneHeal.N > 0 && (f.LastStoneAt < 0 || f.StoneGap < 0 || ctx.Now - f.LastStoneAt >= f.StoneGap);
        if (stoneReady) hp += Math.Max(0, f.StoneHeal.Mean - ConfidenceSd * f.StoneHeal.Sd);
        return (dps > 0 ? hp / dps : double.PositiveInfinity, unknown);
    }
}
