using Fiesta.Bot.Scripting;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>The signal that separates a script doing nothing from one doing its job.
///
/// <para>On 2026-09-13 all four live bots sat at <c>State="running"</c> with <c>Ticks</c> climbing past
/// 23,000 while EVERY tick threw on the first statement of <c>tick()</c>. `SafeCall` catches per tick, so
/// none of the watchdog's existing signals could tell the difference — and MOVEFAIL read 0 because a dead
/// script never walks, which made the outage look like a fix. `ConsecutiveTickErrors` is the field that
/// makes the state observable; these tests pin its contract.</para></summary>
public class ScriptStatusStreakTests
{
    private static ScriptStatus Status(long streak) =>
        new("level_quest", "running", Ticks: 23473, EventsHandled: 0, LastError: "boom",
            UptimeSeconds: 900, Globals: [], SmState: null) { ConsecutiveTickErrors = streak };

    /// <summary>A healthy script reports a zero streak, and zero is a real value here — not "unknown".</summary>
    [Fact]
    public void AHealthyScriptReportsNoStreak()
        => Status(0).ConsecutiveTickErrors.ShouldBe(0);

    /// <summary>The exact shape of the outage: running, ticking, and completely dead.</summary>
    [Fact]
    public void ARunningScriptCanStillBeDead()
    {
        var s = Status(4000);
        s.State.ShouldBe("running");        // what the watchdog used to look at
        s.Ticks.ShouldBeGreaterThan(500);   // and this too
        s.ConsecutiveTickErrors.ShouldBeGreaterThan(0);   // the only field that tells the truth
    }

    /// <summary>The watchdog's threshold, mirrored so a change to either side shows up here. 25 ticks at
    /// 400ms is ~10s: longer than any transient, far shorter than the hours the outage ran.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(24, false)]
    [InlineData(25, true)]
    [InlineData(23473, true)]
    public void TheStreakDecidesWhetherARestartIsDue(long streak, bool shouldRestart)
        => (Status(streak).ConsecutiveTickErrors >= 25).ShouldBe(shouldRestart);

    /// <summary>The record must carry the streak through `with`, since the watchdog pattern-matches on it
    /// alongside the other fields.</summary>
    [Fact]
    public void TheStreakSurvivesRecordCopying()
        => (Status(30) with { State = "error" }).ConsecutiveTickErrors.ShouldBe(30);
}
