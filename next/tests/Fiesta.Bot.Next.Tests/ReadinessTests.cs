using Fiesta.Bot.Api;
using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Character readiness measured against what client data says the character may wear and learn (runs when the
/// client data is on this machine).</summary>
public class ReadinessTests
{
    private static ClientData? Client()
    {
        foreach (var d in new[] { Environment.GetEnvironmentVariable("CLIENT_DATA_DIR"), "Z:/Client2026patched_10_6_6/ressystem" })
            if (d is not null && File.Exists(Path.Combine(d, "ItemInfo.shn"))) return new ClientData(d);
        return null;
    }

    [Fact]
    public void A_level_15_mage_in_the_novice_shirt_is_not_behind_on_its_shirt()
    {
        if (Client() is not { } c) return;
        using var t = new TestBot(c);
        t.Ctx.World.Self.Seed("m", 16, 16, 15);
        var items = new ItemsModule(t.Ctx);
        // no mage body armor exists between level 2 and level 20: the Novice Shirt is the best at 15 (NextM, 02:16)
        Assert.Equal(1503, items.BestWearable(7));
        // the level-8 Magic Hat / Pants / Boots and the level-15 Magic Staff are the best of their slots
        Assert.Equal(1505, items.BestWearable(1));
        Assert.Equal(1506, items.BestWearable(19));
        Assert.Equal(1504, items.BestWearable(21));
        Assert.Equal(1752, items.BestWearable(12));
        // at level 14 the staff is the level-8 Long Staff
        t.Ctx.World.Self.Seed("m", 16, 16, 14);
        Assert.Equal(1751, items.BestWearable(12));
    }

    [Fact]
    public void Skills_behind_counts_class_books_up_to_our_level_and_drops_as_we_learn()
    {
        if (Client() is not { } c) return;
        using var t = new TestBot(c);
        t.Ctx.World.Self.Seed("m", 16, 16, 15);
        var skills = new SkillsModule(t.Ctx);
        var n = skills.Behind();
        Assert.True(n > 0);
        // production lines are not power: the INT Spell Scroll and Beast Potion recipe tiers are not counted (they made
        // the live count 28 for both bots, 02:34)
        Assert.True(n < 20, $"{n} behind");
        // a fresh level-1 mage has fewer books open to it
        t.Ctx.World.Self.Seed("m", 16, 16, 1);
        Assert.True(skills.Behind() < n);
    }
}
