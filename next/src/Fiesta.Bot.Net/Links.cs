namespace Fiesta.Bot.Net;

/// <summary>Which server a link talks to.</summary>
public enum LinkKind : byte { Login, WorldManager, Zone }

/// <summary>Connection state of a link.</summary>
public enum LinkState : byte { Disconnected, Connecting, Handshake, Ready, Closing, Faulted }

/// <summary>A server endpoint (host, port).</summary>
public readonly record struct Endpoint(string Host, int Port);

/// <summary>One decrypted, framed packet as read from the wire. The payload is a pooled buffer slice owned by
/// the receiver until <see cref="Release"/>.</summary>
public readonly struct RawFrame
{
    /// <summary>Opcode = (dept &lt;&lt; 10) | cmd.</summary>
    public ushort Opcode { get; init; }
    /// <summary>Payload bytes (pooled).</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
    /// <summary>Receive timestamp (monotonic ms).</summary>
    public long ReceivedAt { get; init; }
    /// <summary>Return the payload buffer to its pool.</summary>
    public void Release() => throw new NotImplementedException();
}

/// <summary>Sink for frames read by a link's receive loop (runs on an I/O thread, must not block).</summary>
public interface IFrameSink
{
    /// <summary>Called for every inbound frame, in order.</summary>
    void OnFrame(in RawFrame frame);
    /// <summary>Called once when the link closes or faults.</summary>
    void OnClosed(LinkState finalState, Exception? error);
}

/// <summary>Source of outbound frames the link's writer loop drains (the tick enqueues, the writer sends).</summary>
public interface IOutboundSource
{
    /// <summary>Wait (on the I/O side) until at least one frame is queued.</summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken ct);
    /// <summary>Dequeue one frame to send; false when empty.</summary>
    bool TryDequeue(out ushort opcode, out ReadOnlyMemory<byte> payload);
}

/// <summary>A transport link: socket + cipher + framing, with a receive loop and a writer loop that never involve
/// the tick thread.</summary>
public interface ILink : IAsyncDisposable
{
    /// <summary>What this link connects to.</summary>
    LinkKind Kind { get; }
    /// <summary>Current state.</summary>
    LinkState State { get; }
    /// <summary>Remote endpoint.</summary>
    Endpoint Remote { get; }
    /// <summary>Connect, run the handshake (seed), then start the receive and writer loops.</summary>
    Task ConnectAsync(Endpoint endpoint, IFrameSink sink, IOutboundSource outbound, CancellationToken ct);
    /// <summary>Close gracefully (drain the writer, then shut the socket).</summary>
    Task CloseAsync(TimeSpan drain);
}

/// <summary>Stream cipher state for one direction (the asymmetric XOR table, BYO).</summary>
public interface IStreamCipher
{
    /// <summary>Transform bytes in place and advance the cipher position.</summary>
    void Apply(Span<byte> data);
    /// <summary>Reset to a handshake seed.</summary>
    void Reset(ushort seed);
}

/// <summary>Socket implementation of <see cref="ILink"/> on System.IO.Pipelines.</summary>
public sealed class PipeLink : ILink
{
    /// <summary>Create a link of a kind with the BYO cipher table.</summary>
    public PipeLink(LinkKind kind, ReadOnlyMemory<byte> xorTable) => throw new NotImplementedException();
    /// <inheritdoc/>
    public LinkKind Kind => throw new NotImplementedException();
    /// <inheritdoc/>
    public LinkState State => throw new NotImplementedException();
    /// <inheritdoc/>
    public Endpoint Remote => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task ConnectAsync(Endpoint endpoint, IFrameSink sink, IOutboundSource outbound, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task CloseAsync(TimeSpan drain) => throw new NotImplementedException();
    /// <inheritdoc/>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Frame codec: length prefix, opcode, payload (the existing FiestaPacket framing).</summary>
public static class FrameCodec
{
    /// <summary>Try to read one frame from a buffer; returns bytes consumed or 0 if incomplete.</summary>
    public static int TryRead(ReadOnlySpan<byte> buffer, out ushort opcode, out ReadOnlySpan<byte> payload) => throw new NotImplementedException();
    /// <summary>Write one frame (length, opcode, payload) into a destination; returns bytes written.</summary>
    public static int Write(Span<byte> destination, ushort opcode, ReadOnlySpan<byte> payload) => throw new NotImplementedException();
}

/// <summary>Result of the Login -> WorldManager -> Zone chain.</summary>
public sealed record LoginResult(bool Ok, string? Error, Endpoint Zone, ushort WmHandle, string CharacterName, int Level);

/// <summary>The typed login chain (port of the current Login/), run on I/O threads.</summary>
public interface ILoginChain
{
    /// <summary>Log in, select or create the character, and return the zone endpoint.</summary>
    Task<LoginResult> RunAsync(LoginRequest request, CancellationToken ct);
}

/// <summary>Login parameters.</summary>
public sealed record LoginRequest(Endpoint Login, string Username, string PasswordMd5, string? Character, bool Create, int? ClassId);

/// <summary>Moves a bot between zone links (LINKSAME / LINKOTHER / cross-server) without involving the tick.</summary>
public interface IHandoffCoordinator
{
    /// <summary>Open the new zone link, then retire the old one; raises the agent's MapChanged when ready.</summary>
    Task<ILink> HandoffAsync(ILink current, Endpoint target, CancellationToken ct);
}
