using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Agent;

/// <summary>Why the game refused a move.</summary>
public enum MoveFailKind : byte
{
    /// <summary>Our `from` was more than 200 u from the game's position (adopt the carried position; no wall learned).</summary>
    Desync,
    /// <summary>The segment was refused on a plausible position (a wall the static grid lacks; learn in the bot's overlay).</summary>
    Blocked,
}

/// <summary>What the movement controller is asked to do.</summary>
public sealed record MoveOrder(double X, double Y, bool UsePath, double ArriveWithin, ActionId Action);

/// <summary>Owns MOVERUN/STOP: streams legs at client pace, keeps the believed position within the game's tolerance,
/// classifies MOVEFAIL, tells the combat controller about moves.</summary>
public sealed class MoveController
{
    /// <summary>Start a move (replaces the current one).</summary>
    public void Order(MoveOrder order, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Stop and commit the position.</summary>
    public void Stop(AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Moving now?</summary>
    public bool Moving => throw new NotImplementedException();
    /// <summary>Current waypoint list (for the sensor view).</summary>
    public IReadOnlyList<(uint X, uint Y)> Path => throw new NotImplementedException();
    /// <summary>Step 4 of the tick.</summary>
    public void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Feed MOVEFAIL / speed / position events.</summary>
    public void Apply(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Classify a MOVEFAIL from our last sent `from` and the carried position.</summary>
    public static MoveFailKind Classify((uint X, uint Y) sentFrom, (uint X, uint Y) carried) => throw new NotImplementedException();
}

/// <summary>Owns battle mode, targeting, BASHSTART/STOP and the cast queue (no learned reach: GameRules).</summary>
public sealed class CombatController
{
    /// <summary>Queue a cast; sequencing (target, mode, stop) is handled here.</summary>
    public ActionId Cast(int skillId, int targetHandle, byte layer, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Queue a ground cast.</summary>
    public ActionId CastAt(int skillId, double x, double y, byte layer, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Start auto-attack on a target.</summary>
    public ActionId Bash(int targetHandle, byte layer, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Stop auto-attack.</summary>
    public ActionId StopBash(byte layer, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Called by the move controller: a move cancels casting and stops the bash.</summary>
    public void OnMoved(AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Step 5 of the tick: re-arm the bash after moves, drive queued casts.</summary>
    public void Advance(AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Feed combat events.</summary>
    public void Apply(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
    /// <summary>Server-confirmed target.</summary>
    public int Target => throw new NotImplementedException();
    /// <summary>Bashing now?</summary>
    public bool Bashing => throw new NotImplementedException();
}

/// <summary>Why <see cref="GameRules.CanUse"/> said no (the Lua `skills.R_*` codes).</summary>
public enum CanUseReason : byte
{
    Ok, NotLearned, Cooldown, Sp, Hp, Dead, State, Mounted, Casting, Moving, Weapon, Target, Range, LineOfFire, Map,
}

/// <summary>Weapon class for reach.</summary>
public enum WeaponClass : byte { Melee, Bow, Crossbow, Staff, Wand, None }

/// <summary>The game's rules in one tested place (REVIEW section 5; each confirmed by a live probe).</summary>
public static class GameRules
{
    /// <summary>A player's body size.</summary>
    public const double PlayerBody = 50;
    /// <summary>Melee weapon reach.</summary>
    public const double MeleeReach = 120;
    /// <summary>Bow/crossbow reach.</summary>
    public const double BowReach = 600;
    /// <summary>Maximum distance between our move's `from` and the game's position.</summary>
    public const double MoveFromTolerance = 200;
    /// <summary>A mob further than this from its spawn stops acquiring new targets.</summary>
    public const double HomeAcquireLimit = 3000;

    /// <summary>Body size of a mob (MobInfo AbsoluteSize * 50 / 1000).</summary>
    public static double MobBody(MobInfoRow mob) => throw new NotImplementedException();
    /// <summary>Auto-attack reach: (body + weapon reach) * 1.25.</summary>
    public static double BashReach(double targetBody, WeaponClass weapon) => throw new NotImplementedException();
    /// <summary>Skill reach: r = Range + max(50, Range/10) + body; accepted up to min(r * 1.25, r + 60).</summary>
    public static double SkillReach(ActiveSkillRow skill, double targetBody) => throw new NotImplementedException();
    /// <summary>A move destination is accepted when any tile of its 3x3 block is walkable.</summary>
    public static bool MoveTargetAccepted(BlockLayer layer, double x, double y) => throw new NotImplementedException();
    /// <summary>A straight segment is refused only at two consecutive samples blocked with all four 10 u neighbours blocked.</summary>
    public static bool MoveSegmentAccepted(BlockLayer layer, double fx, double fy, double tx, double ty) => throw new NotImplementedException();
    /// <summary>Line of fire through the attack-block layer.</summary>
    public static bool LineOfFire(BlockLayer layer, double fx, double fy, double tx, double ty) => throw new NotImplementedException();
    /// <summary>Distance left before a chasing mob gives up (FollowCha estimate from its last-hit point).</summary>
    public static double LeashLeft(double mobX, double mobY, double lastHitX, double lastHitY, double chaseDistance) => throw new NotImplementedException();
    /// <summary>The generic usability check behind Lua `canUse` (API_SPEC 2.5.1).</summary>
    public static CanUseReason CanUse(AgentContext ctx, int skillId, int targetHandle) => throw new NotImplementedException();
}

/// <summary>Learners turn observations into knowledge-store samples (server-only facts, client-legal).</summary>
public interface ILearner
{
    /// <summary>Observe one inbound event with the world as context.</summary>
    void Observe(in InboundEvent evt, AgentContext ctx);
}

/// <summary>Detect radius: lower bounds from "noticed us at d", upper bounds from "did not notice us in sight at d".</summary>
public sealed class AggroLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
}

/// <summary>Chase distance from the last-hit point at the moment a mob turns back.</summary>
public sealed class LeashLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
}

/// <summary>Mob reach (largest landed swing distance minus our body) and swing interval.</summary>
public sealed class MobAttackLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
}

/// <summary>Family assist radius: distance of mobs that aggro within 1 s of our first hit on a family member.</summary>
public sealed class AssistLearner : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
}

/// <summary>Least-squares fit of a mob's weapon range and armour from hits taken and dealt (damage formula known).</summary>
public sealed class DamageFitter : ILearner
{
    /// <inheritdoc/>
    public void Observe(in InboundEvent evt, AgentContext ctx) => throw new NotImplementedException();
}
