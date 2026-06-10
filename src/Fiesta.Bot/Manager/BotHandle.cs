using Fiesta.Bot.Session;

namespace Fiesta.Bot.Manager;

/// <summary>Where a bot is in its lifecycle. Advances monotonically through the
/// login chain to <see cref="InZone"/>, then ends at <see cref="Stopped"/>
/// (clean / kicked) or <see cref="Failed"/> (error before or after zone entry).</summary>
public enum BotPhase
{
    Pending,        // queued, lifecycle task not yet running
    LoggingIn,      // Login → WORLDSELECT_ACK
    SelectingChar,  // WM: LOGINWORLD → (create) → CHAR_LOGIN → tutorial decline
    EnteringZone,   // [1801] MAP_LOGIN_REQ
    InZone,         // session running, heartbeats answered
    Stopping,       // stop requested, winding down
    Stopped,        // ended cleanly (cancelled) or peer closed after zone entry
    Failed,         // errored (see Error)
}

/// <summary>
/// One managed bot: its spawn options, lifecycle phase, the running task, and
/// (once in zone) the live <see cref="BotSession"/>s. Owned by <see cref="BotManager"/>.
/// Phase/character/error are written from the lifecycle task and read from HTTP
/// threads, so they're volatile and the log buffer is locked — this is a status
/// surface, snapshot it with <see cref="Snapshot"/>.
/// </summary>
public sealed class BotHandle
{
    private const int MaxLogLines = 200;
    private readonly List<string> _log = new();
    private readonly object _logGate = new();

    private volatile BotPhase _phase = BotPhase.Pending;
    private volatile string? _charName;
    private volatile string? _error;

    internal BotHandle(string id, BotSpawnOptions options)
    {
        Id = id;
        Options = options;
        CreatedAtUtc = DateTime.UtcNow;
        Cts = new CancellationTokenSource();
    }

    public string Id { get; }
    public BotSpawnOptions Options { get; }
    public DateTime CreatedAtUtc { get; }

    public BotPhase Phase => _phase;
    public string? CharName => _charName;
    public string? Error => _error;

    /// <summary>The in-zone session once entered (null until <see cref="BotPhase.InZone"/>).
    /// The WM session is held open alongside it but isn't the status surface.</summary>
    public BotSession? ZoneSession { get; internal set; }

    /// <summary>The zone perception model (nearby players + chat), live once in zone.</summary>
    public ZoneView? ZoneView { get; internal set; }

    internal CancellationTokenSource Cts { get; }
    internal Task? RunTask { get; set; }

    internal void SetPhase(BotPhase phase) => _phase = phase;
    internal void SetCharName(string name) => _charName = name;
    internal void SetError(string error) => _error = error;

    internal void Log(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {message}";
        lock (_logGate)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
    }

    private IReadOnlyList<string> RecentLog()
    {
        lock (_logGate) return _log.ToArray();
    }

    /// <summary>A consistent, serializable point-in-time view for the API.</summary>
    public BotSnapshot Snapshot()
    {
        var state = ZoneSession?.State;
        var view = ZoneView;
        return new BotSnapshot(
            Id: Id,
            Phase: Phase.ToString(),
            Host: Options.Host,
            Username: Options.Credentials.Username,
            Character: CharName,
            Connected: state?.Connected ?? false,
            InboundFrames: state?.InboundCount ?? 0,
            Heartbeats: state?.HeartbeatCount ?? 0,
            LastOpcode: state is { } s ? $"0x{s.LastOpcode:X4}" : null,
            UptimeSeconds: state is { } u ? Math.Round(u.Uptime.TotalSeconds, 1) : 0,
            DisconnectReason: state?.DisconnectReason,
            Error: Error,
            NearbyPlayers: view?.NearbyCount ?? 0,
            LastChat: view?.LastChat is { } c ? $"<{c.SenderName ?? $"h{c.Handle}"}> {c.Text}" : null,
            CreatedAtUtc: CreatedAtUtc,
            RecentLog: RecentLog());
    }
}

/// <summary>Serializable point-in-time view of a bot, returned by the control API.</summary>
public sealed record BotSnapshot(
    string Id,
    string Phase,
    string Host,
    string Username,
    string? Character,
    bool Connected,
    long InboundFrames,
    long Heartbeats,
    string? LastOpcode,
    double UptimeSeconds,
    string? DisconnectReason,
    string? Error,
    int NearbyPlayers,
    string? LastChat,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> RecentLog);
