using Fiesta.Bot.Navigation;
using Fiesta.Bot.Session;

namespace Fiesta.Bot.Manager;

/// <summary>The kind of a <see cref="BotEvent"/> — what happened to/around the bot.
/// Each kind fixes the runtime type of <see cref="BotEvent.Data"/> (see the doc on
/// each member), which the script runner switches on to build the Lua callback arg.</summary>
public enum BotEventKind
{
    /// <summary><see cref="ChatMessage"/> — a nearby/overheard chat line.</summary>
    Chat,
    /// <summary><see cref="ushort"/> — a cast-fail reason code (0x0FC9 SP, 0x0FCA range…).</summary>
    CastFail,
    /// <summary><see cref="NearbyPlayer"/> — a player entered view.</summary>
    PlayerAppeared,
    /// <summary><see cref="ushort"/> — the zone handle of a player that left view.</summary>
    PlayerLeft,
    /// <summary><see cref="MapHandoff"/> — the bot changed map (gate / town portal).</summary>
    MapChanged,
    /// <summary><c>(uint X, uint Y)</c> — the server snapped us back (MOVEFAIL).</summary>
    MoveFailed,
    /// <summary><see cref="uint"/> — the bot's new current HP (HPCHANGE).</summary>
    Hp,
    /// <summary><see cref="uint"/> — the bot's new current SP (SPCHANGE).</summary>
    Sp,
    /// <summary><see cref="HitInfo"/> — a combat hit in view (own swing or others').</summary>
    Hit,
}

/// <summary>One thing that happened to a bot, carried on the stable
/// <see cref="BotHandle.Events"/> hub. <see cref="Data"/>'s type is fixed by
/// <see cref="Kind"/>; the script runner pattern-matches to dispatch the matching
/// Lua callback.</summary>
public sealed record BotEvent(BotEventKind Kind, object? Data);
