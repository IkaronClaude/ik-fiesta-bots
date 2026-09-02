using Fiesta.Bot.GameData;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>Which `MobInfo.Type` values the bot may attack.
///
/// <para>The filter used to be a single value — 9, <c>MobTypes.Herb</c> — so herbs were excluded and every
/// ORE and LOG was fair game. Neither is <c>IsNPC</c> nor <c>IsPlayerSide</c>, so nothing else caught them:
/// MageZero cast `IceBolt01` at a Copper Ore <b>111 times for zero damage</b>, every cast answered
/// `0x0FCD`, and the leveller's UN-KILLABLE crutch burned 30 seconds per node on `WOOD3`.</para>
///
/// <para>These are the game's own `MobType` enum values, verified against `MobInfo.shn`: `MINE1`/`MINE2`
/// "Copper Ore" are type 8, `WOOD3` "Wood" is type 10, and real enemies are 0-5 (Mushroom is
/// MagicLife 1, Imp is Spirit 2, Crab is Beast 3).</para></summary>
public class HuntableMobTypeTests
{
    /// <summary>⭐ The two that were slipping through, and the reason this test exists.</summary>
    [Theory]
    [InlineData(ClientData.MobTypes.Mine, "MINE1/MINE2 -- Copper Ore, what MageZero attacked 111 times")]
    [InlineData(ClientData.MobTypes.Wood, "WOOD3 -- the leveller's UN-KILLABLE crutch target")]
    public void OreAndWoodAreNotHuntable(int mobType, string why)
        => ClientData.NonCombatMobTypes.ShouldContain(mobType, why);

    /// <summary>Herbs stayed excluded — the old single value was right about one of eleven types.</summary>
    [Fact]
    public void HerbsAreStillExcluded()
    {
        ClientData.NonCombatMobTypes.ShouldContain(ClientData.MobTypes.Herb);
        ClientData.ResourceNodeType.ShouldBe(ClientData.MobTypes.Herb, "the old constant still means a herb");
    }

    /// <summary>Untargetable scenery: gates, quiz fields, the KQ gate and the egg.</summary>
    [Theory]
    [InlineData(ClientData.MobTypes.Object)]
    [InlineData(ClientData.MobTypes.NoTarget)]
    [InlineData(ClientData.MobTypes.NoTarget2)]
    public void UntargetableSceneryIsExcluded(int mobType)
        => ClientData.NonCombatMobTypes.ShouldContain(mobType);

    /// <summary>⭐ Real monsters stay huntable. The filter must not over-reach — excluding a combat type
    /// makes a mob invisible to the bot, which is a worse failure than attacking a rock.</summary>
    [Theory]
    [InlineData(ClientData.MobTypes.Human)]
    [InlineData(ClientData.MobTypes.MagicLife)]   // Mushroom
    [InlineData(ClientData.MobTypes.Spirit)]      // Imp
    [InlineData(ClientData.MobTypes.Beast)]       // Crab
    [InlineData(ClientData.MobTypes.Elemental)]
    [InlineData(ClientData.MobTypes.Undead)]
    [InlineData(ClientData.MobTypes.Devil)]
    [InlineData(ClientData.MobTypes.Meta)]
    public void RealMonstersRemainHuntable(int mobType)
        => ClientData.NonCombatMobTypes.ShouldNotContain(mobType);

    /// <summary>⚠️ Types the PDB enum does not name are deliberately left HUNTABLE. `MobInfo.shn` carries
    /// values above 19 (20, 23, 25-32...), and defaulting those to excluded would silently hide real
    /// monsters — the opposite and worse failure.</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(23)]
    [InlineData(28)]
    [InlineData(32)]
    public void UnknownTypesAreNotExcluded(int mobType)
        => ClientData.NonCombatMobTypes.ShouldNotContain(mobType);

    /// <summary>Flags, guild gates and the guild item merchant are all `IsNPC`, so they need no entry
    /// here — and adding one would imply the type alone decides it, which it does not.</summary>
    [Theory]
    [InlineData(ClientData.MobTypes.Flag)]
    [InlineData(ClientData.MobTypes.NoName)]
    [InlineData(ClientData.MobTypes.GldItem)]
    [InlineData(ClientData.MobTypes.Npc)]
    public void NpcBackedTypesAreLeftToTheIsNpcCheck(int mobType)
        => ClientData.NonCombatMobTypes.ShouldNotContain(mobType);
}
