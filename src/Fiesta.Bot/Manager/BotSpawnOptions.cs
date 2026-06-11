using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;

namespace Fiesta.Bot.Manager;

/// <summary>
/// Everything needed to bring one bot from credentials to in-zone: the server
/// endpoint, the account, which character to enter (or create), and where its
/// reference client data lives (for the [1801] checksums). This is the domain
/// input to <see cref="BotManager.Spawn"/> — the HTTP layer maps its request
/// DTO onto this. Mirrors the knobs <c>LoginTestCli</c> exposes, minus the
/// hold timer (a managed bot runs until stopped).
/// </summary>
public sealed record BotSpawnOptions
{
    /// <summary>Public host of the login server (WM/zone reuse it unless the
    /// server advertises a different IP).</summary>
    public required string Host { get; init; }

    /// <summary>Login server port. Defaults to the conventional 9010.</summary>
    public int LoginPort { get; init; } = 9010;

    /// <summary>Account credentials (password already MD5-hashed —
    /// see <see cref="BotCredentials"/>).</summary>
    public required BotCredentials Credentials { get; init; }

    public byte WorldNo { get; init; }

    /// <summary>Avatar slot to enter with. Null = first avatar on the account.</summary>
    public byte? Slot { get; init; }

    /// <summary>If the chosen slot is empty (or the account has no avatars),
    /// create this character in-band first. Null = don't create.</summary>
    public CharacterSpec? CreateSpec { get; init; }

    /// <summary>Client <c>ressystem</c> dir the [1801] data-file checksums are
    /// computed from. Must match the server's data files.</summary>
    public string DataDir { get; init; } = "Z:/ClientProd2/ressystem";

    /// <summary>WM port to use when <c>WORLDSELECT_ACK</c> advertises port 0
    /// (k8s/proxy sometimes does). The advertised port wins when non-zero.</summary>
    public int WmPortFallback { get; init; } = 9013;

    /// <summary>Optional caller-supplied id. When null the manager assigns one.</summary>
    public string? Id { get; init; }

    /// <summary>Fallback start-map short name, used only if the WM avatar list doesn't
    /// report one (e.g. a freshly created character). Normally the real spawn map comes
    /// from <see cref="Login.AvatarSummary.LoginMap"/> (the WM avatar's <c>loginmap</c>),
    /// which the bot then keeps current via transition packets.</summary>
    public string StartMap { get; init; } = "RouN";

    /// <summary>Enable the buff-in-town behavior with this config. Null = the bot
    /// just idles in town (a <see cref="Session.ZoneView"/> still tracks nearby
    /// players + chat for status and on-demand <c>/say</c>).</summary>
    public BuffConfig? Buff { get; init; }

    /// <summary>Log every inbound frame on both the zone and WM links (opcode +
    /// dept/cmd + len + hex preview) — packet introspection. Noisy; off by default.</summary>
    public bool LogInbound { get; init; }
}
