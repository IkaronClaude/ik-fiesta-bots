using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>GameRules against client data and the live probes' recorded answers (REVIEW section 5).</summary>
public sealed class GameRulesTests
{
    /// <summary>Bash reach against a Slime (body 10) is 162.5 u.</summary>
    [Fact(Skip = "skeleton")] public void BashReach_Slime() { }
    /// <summary>Skill reach applies max(50, 10%) and the min(x1.25, +60) tolerance.</summary>
    [Fact(Skip = "skeleton")] public void SkillReach_Tolerance() { }
    /// <summary>A move from more than 200 u off is a desync, not a wall.</summary>
    [Fact(Skip = "skeleton")] public void MoveFail_Classification() { }
    /// <summary>Thin corners pass the two-sample segment rule.</summary>
    [Fact(Skip = "skeleton")] public void MoveSegment_ThinCorner() { }
}

/// <summary>Brain format, linter and runtime semantics (API_SPEC 5).</summary>
public sealed class BrainTests
{
    /// <summary>A non-terminal state with no exit is a lint error.</summary>
    [Fact(Skip = "skeleton")] public void Lint_StateWithoutExit() { }
    /// <summary>hold/cooldown/minDwell stop a two-state flap.</summary>
    [Fact(Skip = "skeleton")] public void Hysteresis_NoThrash() { }
    /// <summary>stuck fires after budget without progress.</summary>
    [Fact(Skip = "skeleton")] public void Stuck_FiresOnNoProgress() { }
    /// <summary>A higher layer's claim preempts a lower layer's action.</summary>
    [Fact(Skip = "skeleton")] public void Claims_Preempt() { }
    /// <summary>Hot swap keeps bb.bot and same-named states.</summary>
    [Fact(Skip = "skeleton")] public void HotSwap_KeepsState() { }
}

/// <summary>Deterministic replay of recorded sessions, including the 15-day tent loop fixture.</summary>
public sealed class ReplayTests
{
    /// <summary>Same recording, same intents and transitions.</summary>
    [Fact(Skip = "skeleton")] public void Replay_IsDeterministic() { }
    /// <summary>The tent loop recording leaves its state via stuck/snap within its budget.</summary>
    [Fact(Skip = "skeleton")] public void TentLoop_Escapes() { }
}

/// <summary>Navigation port (corridor A*, funnel, nudge, goal snap).</summary>
public sealed class NavTests
{
    /// <summary>Share of paths over 1.15x the shortest stays under 2 % on five maps.</summary>
    [Fact(Skip = "skeleton")] public void PathQuality_Budget() { }
    /// <summary>An unwalkable goal snaps to the nearest walkable tile.</summary>
    [Fact(Skip = "skeleton")] public void GoalSnap() { }
}
