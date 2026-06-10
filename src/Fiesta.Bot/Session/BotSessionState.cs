using Fiesta.Bot.Login;

namespace Fiesta.Bot.Session;

/// <summary>
/// Live, mutable state of one bot's in-zone session. Owned by <see cref="BotSession"/>
/// and updated from its read loop; readable from other threads for status/HTTP.
/// Volatile/Interlocked-friendly fields — this is a snapshot surface, not a lock.
/// </summary>
public sealed class BotSessionState
{
    public BotSessionState(string charName, ushort wmHandle, FiestaEndpoint zone)
    {
        CharName = charName;
        WmHandle = wmHandle;
        Zone = zone;
        ConnectedAtUtc = DateTime.UtcNow;
        LastInboundUtc = DateTime.UtcNow;
    }

    public string CharName { get; }
    public ushort WmHandle { get; }
    public FiestaEndpoint Zone { get; }

    public DateTime ConnectedAtUtc { get; }
    public volatile bool Connected = true;

    /// <summary>When the read loop ended, and why (null while running / on clean stop).</summary>
    public DateTime? DisconnectedAtUtc { get; internal set; }
    public string? DisconnectReason { get; internal set; }

    private long _inboundCount;
    private long _heartbeatCount;
    private long _lastOpcode;
    private long _lastInboundTicks;

    public long InboundCount => Interlocked.Read(ref _inboundCount);
    public long HeartbeatCount => Interlocked.Read(ref _heartbeatCount);
    public ushort LastOpcode => (ushort)Interlocked.Read(ref _lastOpcode);

    public DateTime LastInboundUtc
    {
        get => new(Interlocked.Read(ref _lastInboundTicks), DateTimeKind.Utc);
        private set => Interlocked.Exchange(ref _lastInboundTicks, value.Ticks);
    }

    public TimeSpan Uptime => (DisconnectedAtUtc ?? DateTime.UtcNow) - ConnectedAtUtc;

    internal void RecordInbound(ushort opcode)
    {
        Interlocked.Increment(ref _inboundCount);
        Interlocked.Exchange(ref _lastOpcode, opcode);
        LastInboundUtc = DateTime.UtcNow;
    }

    internal void RecordHeartbeat() => Interlocked.Increment(ref _heartbeatCount);
}
