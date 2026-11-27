using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>Gear upgrades count both hands as one: a two-hander against everything in the hands, a shield or one-hander
/// over a two-hander together with the bag's best piece for the other hand.</summary>
public class GearTests
{
    private const int OffHand = 10, MainHand = 12, Feet = 19;   // equip slots of the test items, as their ItemInfo rows name them
    private const int Axe = 1, Shield = 2, Sword = 3, GoodSword = 4, Boots = 5, BetterBoots = 6;

    private static ItemInfoRow Gear(int id, string inx, int equip, int weaponType, int wc, int ac = 0, bool twoHand = false, int shieldAc = 0) =>
        new(id, inx, inx, 0, 0, equip, 1, weaponType, 1, 1, 1, 0, "-") { MinWc = wc, MaxWc = wc, Ac = ac, TwoHand = twoHand, ShieldAc = shieldAc };

    private static TestBot Bot()
    {
        var d = new TestData();
        d.Items[Axe] = Gear(Axe, "SteelAxe", MainHand, 5, 115, twoHand: true);
        d.Items[Shield] = Gear(Shield, "WoodShield", OffHand, 0, 0, ac: 40, shieldAc: 40);
        d.Items[Sword] = Gear(Sword, "SunBroadsword", MainHand, 1, 60);
        d.Items[GoodSword] = Gear(GoodSword, "FineSword", MainHand, 1, 90);
        d.Items[Boots] = Gear(Boots, "Boots", Feet, 0, 0, ac: 5);
        d.Items[BetterBoots] = Gear(BetterBoots, "BetterBoots", Feet, 0, 0, ac: 9);
        return new TestBot(d);
    }

    private static void Wear(TestBot t, int slot, int item) => t.Ev(InboundKind.ItemCell, a: 8, b: slot, c: item);
    private static void Bag(TestBot t, int slot, int item) => t.Ev(InboundKind.ItemCell, a: 9, b: slot, c: item, x: 1);

    [Fact]
    public void A_shield_or_weaker_one_hander_does_not_strip_a_worn_two_hander()
    {
        using var t = Bot();
        Wear(t, MainHand, Axe); Bag(t, 0, Shield); Bag(t, 1, Sword);
        t.Tick();
        var items = new Fiesta.Bot.Api.ItemsModule(t.Ctx);
        Assert.False(items.IsUpgrade(Shield));   // 40 + best other-hand piece 60 = 100 < 115
        Assert.False(items.IsUpgrade(Sword));    // 60 + shield 40 = 100 < 115
        Assert.Equal(-1, items.EquipUpgrade());
        Assert.Equal(-15, items.UpgradeGain(Shield));
    }

    [Fact]
    public void A_better_pair_replaces_the_two_hander()
    {
        using var t = Bot();
        Wear(t, MainHand, Axe); Bag(t, 0, Shield); Bag(t, 1, GoodSword);
        t.Tick();
        var items = new Fiesta.Bot.Api.ItemsModule(t.Ctx);
        Assert.True(items.IsUpgrade(Shield));    // 40 + 90 = 130 > 115
        Assert.Equal(15, items.UpgradeGain(Shield));
    }

    [Fact]
    public void A_two_hander_is_weighed_against_both_hands()
    {
        using var t = Bot();
        Wear(t, MainHand, Sword); Wear(t, OffHand, Shield); Bag(t, 0, Axe);
        t.Tick();
        var items = new Fiesta.Bot.Api.ItemsModule(t.Ctx);
        Assert.True(items.IsUpgrade(Axe));       // 115 > 60 + 40
        Assert.Equal(15, items.UpgradeGain(Axe));

        Wear(t, MainHand, GoodSword);
        t.Tick();
        Assert.False(items.IsUpgrade(Axe));      // 115 < 90 + 40: the pair stays on, no flip-flop
    }

    [Fact]
    public void Other_slots_still_compare_with_the_piece_in_their_own_slot()
    {
        using var t = Bot();
        Wear(t, MainHand, Axe); Wear(t, Feet, Boots); Bag(t, 0, BetterBoots);
        t.Tick();
        var items = new Fiesta.Bot.Api.ItemsModule(t.Ctx);
        Assert.True(items.IsUpgrade(BetterBoots));
        Assert.Equal(4, items.UpgradeGain(BetterBoots));
        Assert.Equal(0, items.EquipUpgrade());
    }
}
