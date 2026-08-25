using System;
using System.Collections.Generic;

namespace Fiesta.Bot.Combat;

/// <summary>One parameter block: a value for every <see cref="Stat"/>.
///
/// Fixed at 51 slots because that is what the server's block is; <see cref="Stat"/>'s ordinal is the index.</summary>
public sealed class StatBlock
{
    /// <summary>Slots per block. Matches <c>Parameter::Container</c>'s 0xCC-byte block exactly.</summary>
    public const int SlotCount = 51;

    private readonly int[] _values = new int[SlotCount];

    public int this[Stat stat]
    {
        get => _values[(int)stat];
        set => _values[(int)stat] = value;
    }

    /// <summary>Set every slot to <paramref name="value"/>. Used to make a whole rate half neutral.</summary>
    public StatBlock Fill(int value)
    {
        Array.Fill(_values, value);
        return this;
    }

    public StatBlock CopyFrom(StatBlock other)
    {
        Array.Copy(other._values, _values, SlotCount);
        return this;
    }
}

/// <summary>Everything the damage engine needs to know about one combatant's stats.
///
/// Mirrors the server's <c>Parameter::Container</c>: a <see cref="Base"/> block plus, for each
/// <see cref="StatModifier"/>, a flat <see cref="Plus"/> half and a permille <see cref="Rate"/> half.
///
/// <para><b>A permille rate of 1000 means "unchanged".</b> <see cref="Unmodified"/> builds a container in
/// that state — every rate 1000, every plus 0 — which is what a plain field mob looks like. Building one
/// with <c>new</c> would leave the rates at zero and multiply every stat to nothing, so always start from
/// a factory.</para></summary>
public sealed class CombatStats
{
    /// <summary>A rate half of this value leaves the stat unchanged. Rates are permille throughout.</summary>
    public const int NeutralRate = 1000;

    private readonly StatBlock[] _plus;
    private readonly StatBlock[] _rate;

    private CombatStats()
    {
        var sources = Enum.GetValues<StatModifier>().Length;
        _plus = new StatBlock[sources];
        _rate = new StatBlock[sources];
        for (var i = 0; i < sources; i++)
        {
            _plus[i] = new StatBlock();
            _rate[i] = new StatBlock();
        }
    }

    /// <summary>The character's own stats, before any modifier layer. The server calls this
    /// <c>PureCharParam</c>. For a mob this is the whole story: its <c>MobInfoServer</c> / <c>MobWeapon</c>
    /// row IS this block.</summary>
    public StatBlock Base { get; } = new();

    /// <summary>The flat bonus contributed by one source.</summary>
    public StatBlock Plus(StatModifier source) => _plus[(int)source];

    /// <summary>The permille multiplier contributed by one source. 1000 = unchanged.</summary>
    public StatBlock Rate(StatModifier source) => _rate[(int)source];

    /// <summary>A container with no modifiers: every plus 0, every rate <see cref="NeutralRate"/>.</summary>
    public static CombatStats Unmodified()
    {
        var stats = new CombatStats();
        foreach (var source in Enum.GetValues<StatModifier>())
            stats.Rate(source).Fill(NeutralRate);
        return stats;
    }

    /// <summary>An unmodified container whose <see cref="Base"/> block is filled from
    /// <paramref name="baseStats"/>.
    ///
    /// This is the convenient entry point for a mob, whose stats really are just a flat table — read a
    /// <c>MobInfoServer</c> / <c>MobWeapon</c> row into a dictionary and hand it over. Players need the
    /// modifier layers as well, so they should populate <see cref="Plus"/> / <see cref="Rate"/> directly.</summary>
    public static CombatStats FromBaseStats(IEnumerable<KeyValuePair<Stat, int>> baseStats)
    {
        var stats = Unmodified();
        foreach (var (stat, value) in baseStats)
            stats.Base[stat] = value;
        return stats;
    }

    /// <summary>A deep copy — safe to mutate without disturbing the original. Useful for asking
    /// "what would this hit for if I had one more buff?".</summary>
    public CombatStats Clone()
    {
        var copy = new CombatStats();
        copy.Base.CopyFrom(Base);
        foreach (var source in Enum.GetValues<StatModifier>())
        {
            copy.Plus(source).CopyFrom(Plus(source));
            copy.Rate(source).CopyFrom(Rate(source));
        }
        return copy;
    }
}
