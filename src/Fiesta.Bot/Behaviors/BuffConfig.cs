namespace Fiesta.Bot.Behaviors;

/// <summary>
/// Configures the buff-in-town behavior: when to buff and with which skills.
/// Buff skills must already be <i>learnt</i> on the character (in Fiesta that
/// means money → buy the skill scroll → learn it; a fresh test priest has none),
/// so an empty <see cref="SkillIds"/> means "react to requests, but there's
/// nothing to cast yet" — the behavior logs the request and no-ops.
/// </summary>
public sealed record BuffConfig
{
    /// <summary>Case-insensitive substring that triggers a buff when overheard in
    /// chat (e.g. "buff pls" matches "buff"). </summary>
    public string Trigger { get; init; } = "buff";

    /// <summary>Skill IDs to cast, in order, on the target. Empty = none learnt yet.</summary>
    public IReadOnlyList<ushort> SkillIds { get; init; } = [];

    /// <summary>Also buff players as they walk into view, not just on request.</summary>
    public bool AutoBuffNearby { get; init; }

    /// <summary>Don't re-buff the same target within this window.</summary>
    public TimeSpan TargetCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay between successive skill casts on one target (cast pacing).</summary>
    public TimeSpan CastInterval { get; init; } = TimeSpan.FromMilliseconds(600);
}
