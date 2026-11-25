using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>The job change chain read from the client's quest table (runs when the client data is on this machine).</summary>
public class JobChangeTests
{
    private static ClientData? Client()
    {
        foreach (var d in new[] { Environment.GetEnvironmentVariable("CLIENT_DATA_DIR"), "Z:/Client2026patched_10_6_6/ressystem" })
            if (d is not null && File.Exists(Path.Combine(d, "QuestData.shn"))) return new ClientData(d);
        return null;
    }

    [Fact]
    public void The_promotion_quests_start_a_scenario_and_their_chain_is_found()
    {
        if (Client() is not { } c) return;
        // Road to Promotion for the fighter / archer / mage lines: [SCRIPT_START] ACCEPT, SCENARIO 8
        foreach (var (q, cls) in new[] { (60006, 1), (60011, 11), (60012, 16) })
        {
            var d = c.Quest(q)!;
            Assert.Equal(8, d.Scenario);
            Assert.Equal(cls, d.StartClass);
            Assert.Equal(60017, d.PrevQuest);
        }
        Assert.Equal(-1, c.Quest(60015)!.Scenario);
        // the chain before it: 60017 <- 60016 <- 60015 (no previous quest)
        Assert.Equal(60016, c.Quest(60017)!.PrevQuest);
        Assert.Equal(60015, c.Quest(60016)!.PrevQuest);
        Assert.Equal(0, c.Quest(60015)!.PrevQuest);
        Assert.Equal(20, c.Quest(60015)!.MinLevel);
    }
}
