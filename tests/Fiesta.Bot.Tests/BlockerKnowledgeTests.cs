using System;
using System.IO;
using Fiesta.Bot.Manager;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>
/// Persisted blockers DEFER a retest, they never ban (operator 2026-10-05: a stored failure "risks locking a quest up
/// forever even if the blocker is temporary"). These pin the three ways out: the backoff expires, a level-up, a success.
/// </summary>
public class BlockerKnowledgeTests
{
    private static string Dir() => Path.Combine(Path.GetTempPath(), "blockers-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unknown_blocker_is_tested_now()
    {
        new NpcKnowledge(Dir()).BlockerRetestInSec("bot", "scenario:10", 20).ShouldBe(0);
    }

    [Fact]
    public void A_failure_defers_the_retest_by_about_an_hour()
    {
        var k = new NpcKnowledge(Dir());
        k.RecordBlocker("bot", "scenario:10", 20).ShouldBe(3600);
        k.BlockerRetestInSec("bot", "scenario:10", 20).ShouldBeInRange(3500, 3600);
    }

    [Fact]
    public void Each_further_failure_doubles_the_backoff_up_to_a_day()
    {
        var k = new NpcKnowledge(Dir());
        k.RecordBlocker("bot", "x", 20).ShouldBe(3600);
        k.RecordBlocker("bot", "x", 20).ShouldBe(7200);
        k.RecordBlocker("bot", "x", 20).ShouldBe(14400);
        for (int i = 0; i < 10; i++) k.RecordBlocker("bot", "x", 20);
        k.RecordBlocker("bot", "x", 20).ShouldBe(86400);
    }

    [Fact]
    public void A_level_up_makes_it_testable_at_once_and_restarts_the_backoff()
    {
        var k = new NpcKnowledge(Dir());
        k.RecordBlocker("bot", "x", 20);
        k.RecordBlocker("bot", "x", 20);
        k.BlockerRetestInSec("bot", "x", 21).ShouldBe(0);
        k.RecordBlocker("bot", "x", 21).ShouldBe(3600);
    }

    [Fact]
    public void Success_clears_it()
    {
        var k = new NpcKnowledge(Dir());
        k.RecordBlocker("bot", "x", 20);
        k.ClearBlocker("bot", "x").ShouldBeTrue();
        k.BlockerRetestInSec("bot", "x", 20).ShouldBe(0);
    }

    [Fact]
    public void It_survives_a_restart_and_is_per_bot()
    {
        var dir = Dir();
        new NpcKnowledge(dir).RecordBlocker("botA", "x", 20);
        var k = new NpcKnowledge(dir);
        k.BlockerRetestInSec("botA", "x", 20).ShouldBeGreaterThan(0);
        k.BlockerRetestInSec("botB", "x", 20).ShouldBe(0);
    }
}
