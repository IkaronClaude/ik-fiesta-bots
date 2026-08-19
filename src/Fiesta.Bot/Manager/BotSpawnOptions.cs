using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;

namespace Fiesta.Bot.Manager;

/// <summary>Everything needed to bring one bot from credentials to in-zone: the server endpoint, the account, which charac…</summary>
public sealed record BotSpawnOptions
{
    /// <summary>Public host of the login server (WM/zone reuse it unless the server advertises a different IP)</summary>
    public required string Host { get; init; }

    /// <summary>Login server port. Defaults to the conventional 9010</summary>
    public int LoginPort { get; init; } = 9010;

    /// <summary>Account credentials (password already MD5-hashed — see )</summary>
    public required BotCredentials Credentials { get; init; }

    public byte WorldNo { get; init; }

    /// <summary>Avatar slot to enter with</summary>
    public byte? Slot { get; init; }

    /// <summary>Character to enter with, selected BY NAME from the WM avatar list — the stable, deterministic identifier (slot…</summary>
    public string? Character { get; init; }

    /// <summary>If the chosen slot is empty (or the account has no avatars), create this character in-band first</summary>
    public CharacterSpec? CreateSpec { get; init; }

    /// <summary>Client ressystem dir the [1801] data-file checksums are computed from</summary>
    public string DataDir { get; init; } = "Z:/ClientProd2/ressystem";

    /// <summary>WM port to use when WORLDSELECT_ACK advertises port 0 (k8s/proxy sometimes does)</summary>
    public int WmPortFallback { get; init; } = 9013;

    /// <summary>Optional caller-supplied id</summary>
    public string? Id { get; init; }

    /// <summary>Fallback start-map short name, used only if the WM avatar list doesn't report one</summary>
    public string StartMap { get; init; } = "RouN";

    /// <summary>Enable the buff-in-town behavior with this config</summary>
    public BuffConfig? Buff { get; init; }

    /// <summary>Log every inbound frame on both the zone and WM links (opcode + dept/cmd + len + hex preview) — packet introsp…</summary>
    public bool LogInbound { get; init; }

    /// <summary>Start the tailable both-directions packet dump from the VERY FIRST connection (login → WM → zone), so the logi…</summary>
    public bool PacketLog { get; init; }

    /// <summary>Narrate phase/tactic changes and cast failures into GAME CHAT. Lives HERE rather than only on the
    /// handle because the handle is rebuilt on every respawn and pod rollout, so a runtime-only flag silently
    /// reverts to off exactly when you are mid-observation and least expecting it. The roster persists this record,
    /// so the setting survives a reconnect the same way PacketLog does.</summary>
    public bool Announce { get; init; }
}
