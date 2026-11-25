using System.Buffers;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Fiesta.Bot.Net;

/// <summary>Which server a link talks to.</summary>
public enum LinkKind : byte { Login, WorldManager, Zone }

/// <summary>Connection state of a link.</summary>
public enum LinkState : byte { Disconnected, Connecting, Handshake, Ready, Closing, Faulted }

/// <summary>A server endpoint (host, port).</summary>
public readonly record struct Endpoint(string Host, int Port)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>One decrypted, framed packet as read from the wire. The payload is only valid during the
/// <see cref="IFrameSink.OnFrame"/> call (it is a slice of the link's receive buffer); a sink that keeps bytes copies them.</summary>
public readonly struct RawFrame
{
    /// <summary>Opcode = (dept &lt;&lt; 10) | cmd.</summary>
    public ushort Opcode { get; init; }
    /// <summary>Payload bytes (borrowed).</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
    /// <summary>Receive timestamp (monotonic ms).</summary>
    public long ReceivedAt { get; init; }
    /// <summary>Department (opcode &gt;&gt; 10).</summary>
    public int Department => Opcode >> 10;
    /// <summary>Borrowed frames need no release; kept for API symmetry with pooled frames.</summary>
    public void Release() { }
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
    /// <summary>Dequeue one frame to send; false when empty. The payload is owned by the source until the next call.</summary>
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

/// <summary>The client-to-server XOR stream cipher: each body byte is XOR-ed with the BYO table, the table position
/// advancing per byte from the handshake seed. Server-to-client frames are plaintext.</summary>
public sealed class XorStreamCipher : IStreamCipher
{
    private readonly byte[] _table;
    private int _pos;

    /// <summary>Create over a table (never empty).</summary>
    public XorStreamCipher(byte[] table, int seed = 0)
    {
        if (table is null || table.Length == 0) throw new ArgumentException("XOR table must be non-empty", nameof(table));
        _table = table;
        Reset((ushort)seed);
    }

    /// <summary>Current table position.</summary>
    public int Position => _pos;

    /// <inheritdoc/>
    public void Reset(ushort seed) => _pos = seed % _table.Length;

    /// <inheritdoc/>
    public void Apply(Span<byte> data)
    {
        var tbl = _table; var n = tbl.Length; var pos = _pos;
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= tbl[pos];
            if (++pos >= n) pos -= n;
        }
        _pos = pos;
    }
}

/// <summary>Loads the BYO XOR table (XOR_TABLE_HEX or XOR_TABLE_PATH: raw bytes or hex text).</summary>
public static class XorTableLoader
{
    /// <summary>From the environment, null when unset.</summary>
    public static byte[]? FromEnvironment()
    {
        var hex = Environment.GetEnvironmentVariable("XOR_TABLE_HEX");
        if (!string.IsNullOrWhiteSpace(hex)) return ParseHex(hex) ?? throw new InvalidOperationException("XOR_TABLE_HEX is not valid hex");
        var path = Environment.GetEnvironmentVariable("XOR_TABLE_PATH");
        return string.IsNullOrWhiteSpace(path) ? null : FromFile(path);
    }

    /// <summary>Load a table file (raw or hex text).</summary>
    public static byte[] FromFile(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"XOR table '{path}' does not exist");
        var bytes = File.ReadAllBytes(path);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        return LooksLikeHex(bytes) && ParseHex(text) is { } parsed ? parsed : bytes;
    }

    private static bool LooksLikeHex(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        foreach (var b in bytes)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)'x' or (byte)'X') continue;
            if (!Uri.IsHexDigit((char)b)) return false;
        }
        return true;
    }

    private static byte[]? ParseHex(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') continue;
            if (c == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'X') { i++; continue; }
            sb.Append(c);
        }
        if (sb.Length == 0 || (sb.Length & 1) != 0) return null;
        try { return Convert.FromHexString(sb.ToString()); } catch (FormatException) { return null; }
    }
}

/// <summary>Frame codec: length prefix (1 byte, or 0 + u16 for bodies over 255), opcode u16, payload.</summary>
public static class FrameCodec
{
    /// <summary>The handshake frame (S->C): opcode 0x0807, payload = seed u16.</summary>
    public const ushort HandshakeOpcode = 0x0807;

    /// <summary>Try to read one frame from a buffer; returns bytes consumed or 0 if incomplete; -1 on a malformed frame.</summary>
    public static int TryRead(ReadOnlySpan<byte> buffer, out ushort opcode, out ReadOnlySpan<byte> payload)
    {
        opcode = 0; payload = default;
        if (buffer.Length < 1) return 0;
        int head, len;
        if (buffer[0] != 0) { head = 1; len = buffer[0]; }
        else
        {
            if (buffer.Length < 3) return 0;
            head = 3; len = buffer[1] | (buffer[2] << 8);
        }
        if (len < 2) return -1;
        if (buffer.Length < head + len) return 0;
        opcode = (ushort)(buffer[head] | (buffer[head + 1] << 8));
        payload = buffer.Slice(head + 2, len - 2);
        return head + len;
    }

    /// <summary>Bytes a frame of a payload length takes on the wire.</summary>
    public static int Size(int payloadLength) => (2 + payloadLength) <= 0xFF ? 3 + payloadLength : 5 + payloadLength;

    /// <summary>Write one frame (length, opcode, payload) into a destination; returns bytes written. The cipher, when
    /// given, is applied to the body (opcode + payload), not the length prefix.</summary>
    public static int Write(Span<byte> destination, ushort opcode, ReadOnlySpan<byte> payload, IStreamCipher? cipher = null)
    {
        var body = 2 + payload.Length;
        int head;
        if (body <= 0xFF) { destination[0] = (byte)body; head = 1; }
        else { destination[0] = 0; destination[1] = (byte)body; destination[2] = (byte)(body >> 8); head = 3; }
        destination[head] = (byte)opcode; destination[head + 1] = (byte)(opcode >> 8);
        payload.CopyTo(destination[(head + 2)..]);
        cipher?.Apply(destination.Slice(head, body));
        return head + body;
    }
}

/// <summary>Tap for every frame in both directions (C->S before encryption); used by the packet capture.</summary>
public delegate void FrameTap(bool outbound, ushort opcode, ReadOnlySpan<byte> payload);

/// <summary>A framed, enciphered TCP connection to one server. Two ways to use it: request/response style for the
/// login chain (<see cref="ReadAsync"/> / <see cref="SendAsync"/>, off the tick), and loop style for a zone
/// (<see cref="RunLoopsAsync"/>: a receive loop feeding an <see cref="IFrameSink"/> and a writer loop draining an
/// <see cref="IOutboundSource"/>). Never used from a tick thread.</summary>
public sealed class FiestaSocket : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _xorTable;
    private XorStreamCipher? _send;
    private byte[] _rx = new byte[64 * 1024];
    private int _rxStart, _rxEnd;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private byte[] _tx = new byte[8 * 1024];
    private volatile bool _closed;

    /// <summary>Remote endpoint.</summary>
    public Endpoint Remote { get; }
    /// <summary>The handshake seed (after <see cref="ConnectAsync"/>).</summary>
    public ushort Seed { get; private set; }
    /// <summary>Optional tap.</summary>
    public FrameTap? Tap { get; set; }
    /// <summary>Monotonic clock for receive stamps.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;
    /// <summary>Frames sent / received.</summary>
    public long FramesIn, FramesOut;

    private FiestaSocket(Socket s, byte[] xor, Endpoint remote) { _socket = s; _xorTable = xor; Remote = remote; }

    /// <summary>Connect and wait for the server's handshake (seed), which arms the send cipher. Frames that arrive
    /// before the handshake are discarded.</summary>
    public static async Task<FiestaSocket> ConnectAsync(Endpoint ep, byte[] xorTable, CancellationToken ct)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await s.ConnectAsync(ep.Host, ep.Port, cts.Token);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            }
            catch (SocketException) { }
            var fs = new FiestaSocket(s, xorTable, ep);
            while (true)
            {
                var f = await fs.ReadAsync(cts.Token);
                if (f.Opcode == FrameCodec.HandshakeOpcode && f.Payload.Length >= 2)
                {
                    fs.Seed = (ushort)(f.Payload[0] | (f.Payload[1] << 8));
                    fs._send = new XorStreamCipher(xorTable, fs.Seed);
                    return fs;
                }
            }
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>Read one frame (copied; for the request/response phase only).</summary>
    public async Task<(ushort Opcode, byte[] Payload)> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var n = FrameCodec.TryRead(_rx.AsSpan(_rxStart, _rxEnd - _rxStart), out var op, out var payload);
            if (n < 0) throw new InvalidDataException("malformed frame");
            if (n > 0)
            {
                var copy = payload.ToArray();
                _rxStart += n;
                FramesIn++;
                Tap?.Invoke(false, op, copy);
                return (op, copy);
            }
            await FillAsync(ct);
        }
    }

    /// <summary>Read frames until one of the opcodes arrives or the timeout elapses (null). Others are passed to
    /// <paramref name="other"/> (may answer, e.g. a heartbeat).</summary>
    public async Task<(ushort Opcode, byte[] Payload)?> WaitForAsync(ReadOnlyMemory<ushort> opcodes, TimeSpan timeout,
        CancellationToken ct, Func<ushort, byte[], Task>? other = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var f = await ReadAsync(cts.Token);
                if (opcodes.Span.Contains(f.Opcode)) return f;
                if (other is not null) await other(f.Opcode, f.Payload);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private async Task FillAsync(CancellationToken ct)
    {
        if (_rxStart > 0)
        {
            Buffer.BlockCopy(_rx, _rxStart, _rx, 0, _rxEnd - _rxStart);
            _rxEnd -= _rxStart; _rxStart = 0;
        }
        if (_rxEnd == _rx.Length) Array.Resize(ref _rx, _rx.Length * 2);
        var read = await _socket.ReceiveAsync(_rx.AsMemory(_rxEnd), SocketFlags.None, ct);
        if (read == 0) throw new EndOfStreamException("peer closed the connection");
        _rxEnd += read;
    }

    /// <summary>Encipher and send one frame (thread-safe; the cipher position is advanced under the lock).</summary>
    public async ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_send is null) throw new InvalidOperationException("send before handshake");
        if (_closed) throw new ObjectDisposedException(nameof(FiestaSocket));
        await _sendLock.WaitAsync(ct);
        try
        {
            Tap?.Invoke(true, opcode, payload.Span);
            var size = FrameCodec.Size(payload.Length);
            if (_tx.Length < size) _tx = new byte[Math.Max(size, _tx.Length * 2)];
            var n = FrameCodec.Write(_tx, opcode, payload.Span, _send);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var sent = 0;
            while (sent < n) sent += await _socket.SendAsync(_tx.AsMemory(sent, n - sent), SocketFlags.None, cts.Token);
            FramesOut++;
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Run the receive loop (frames to the sink, borrowed slices) and the writer loop (drains the outbound
    /// source) until the socket closes or <paramref name="ct"/> is cancelled. Calls <see cref="IFrameSink.OnClosed"/> once.</summary>
    public async Task RunLoopsAsync(IFrameSink sink, IOutboundSource outbound, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? error = null;
        var writer = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested && await outbound.WaitToReadAsync(linked.Token))
                    while (outbound.TryDequeue(out var op, out var payload))
                        await SendAsync(op, payload, linked.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { error ??= e; linked.Cancel(); }
        }, CancellationToken.None);
        try
        {
            // frames already buffered by the request/response phase go first
            while (!linked.IsCancellationRequested)
            {
                var n = FrameCodec.TryRead(_rx.AsSpan(_rxStart, _rxEnd - _rxStart), out var op, out var payload);
                if (n < 0) throw new InvalidDataException("malformed frame");
                if (n == 0) { await FillAsync(linked.Token); continue; }
                var mem = _rx.AsMemory(_rxStart + (n - payload.Length), payload.Length);
                _rxStart += n;
                FramesIn++;
                Tap?.Invoke(false, op, mem.Span);
                sink.OnFrame(new RawFrame { Opcode = op, Payload = mem, ReceivedAt = Clock() });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { error ??= e; }
        linked.Cancel();
        try { await writer; } catch { }
        _closed = true;
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        sink.OnClosed(error is null ? LinkState.Disconnected : LinkState.Faulted, error);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _closed = true;
        try { _socket.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Socket implementation of <see cref="ILink"/> for a zone (wraps <see cref="FiestaSocket"/>).</summary>
public sealed class PipeLink : ILink
{
    private readonly byte[] _xor;
    private FiestaSocket? _sock;
    private Task? _loops;
    private CancellationTokenSource? _cts;
    private volatile LinkState _state;

    /// <summary>Create a link of a kind with the BYO cipher table.</summary>
    public PipeLink(LinkKind kind, ReadOnlyMemory<byte> xorTable) { Kind = kind; _xor = xorTable.ToArray(); }
    /// <summary>Adopt an already-connected socket (the login chain hands its last socket over).</summary>
    public PipeLink(LinkKind kind, FiestaSocket connected) { Kind = kind; _xor = Array.Empty<byte>(); _sock = connected; Remote = connected.Remote; _state = LinkState.Handshake; }
    /// <inheritdoc/>
    public LinkKind Kind { get; }
    /// <inheritdoc/>
    public LinkState State => _state;
    /// <inheritdoc/>
    public Endpoint Remote { get; private set; }
    /// <summary>The socket (after connect).</summary>
    public FiestaSocket? Socket => _sock;

    /// <inheritdoc/>
    public async Task ConnectAsync(Endpoint endpoint, IFrameSink sink, IOutboundSource outbound, CancellationToken ct)
    {
        _state = LinkState.Connecting;
        Remote = endpoint;
        try { _sock ??= await FiestaSocket.ConnectAsync(endpoint, _xor, ct); }
        catch { _state = LinkState.Faulted; throw; }
        Start(sink, outbound);
    }

    /// <summary>Start the loops on an adopted socket.</summary>
    public void Start(IFrameSink sink, IOutboundSource outbound)
    {
        if (_sock is null) throw new InvalidOperationException("not connected");
        _state = LinkState.Ready;
        _cts = new CancellationTokenSource();
        var s = _sock;
        _loops = Task.Run(() => s.RunLoopsAsync(new StateSink(this, sink), outbound, _cts.Token));
    }

    private sealed class StateSink(PipeLink link, IFrameSink inner) : IFrameSink
    {
        public void OnFrame(in RawFrame frame) => inner.OnFrame(frame);
        public void OnClosed(LinkState finalState, Exception? error) { link._state = finalState; inner.OnClosed(finalState, error); }
    }

    /// <inheritdoc/>
    public async Task CloseAsync(TimeSpan drain)
    {
        _state = LinkState.Closing;
        if (drain > TimeSpan.Zero) await Task.Delay(drain);
        _cts?.Cancel();
        if (_loops is not null) try { await _loops.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        if (_sock is not null) await _sock.DisposeAsync();
        _state = LinkState.Disconnected;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(CloseAsync(TimeSpan.Zero));
}

/// <summary>Result of the Login -> WorldManager chain: the zone to enter plus the live WM socket (kept open for the
/// session).</summary>
public sealed record LoginResult(bool Ok, string? Error, Endpoint Zone, ushort WmHandle, string CharacterName, int Level,
    int ClassId = 0, string LoginMap = "")
{
    /// <summary>The world manager socket (stays open while the character plays).</summary>
    public FiestaSocket? WorldManager { get; init; }
}

/// <summary>The login chain (one implementation per wire profile), run off the tick.</summary>
public interface ILoginChain
{
    /// <summary>Log in, select or create the character, and return the zone endpoint.</summary>
    Task<LoginResult> RunAsync(LoginRequest request, CancellationToken ct);
}

/// <summary>Login parameters.</summary>
public sealed record LoginRequest(Endpoint Login, string Username, string PasswordMd5, string? Character, bool Create, int? ClassId)
{
    /// <summary>World number to select.</summary>
    public byte World { get; init; }
    /// <summary>Character slot to use when creating.</summary>
    public byte? Slot { get; init; }
    /// <summary>Use the login host instead of advertised addresses that do not hairpin (local stacks).</summary>
    public bool PreferLoginHost { get; init; } = true;
    /// <summary>Progress log (off the tick).</summary>
    public Action<string>? Log { get; init; }
    /// <summary>Frame tap for every login/WM frame.</summary>
    public FrameTap? Tap { get; init; }
    /// <summary>Male (character creation).</summary>
    public bool Male { get; init; } = true;
}

/// <summary>A login chain failure with the server's reason.</summary>
public sealed class LoginException(string message) : Exception(message);

/// <summary>Moves a bot between zone links (LINKSAME / LINKOTHER / cross-server) without involving the tick.</summary>
public interface IHandoffCoordinator
{
    /// <summary>Open the new zone link, then retire the old one; raises the agent's MapChanged when ready.</summary>
    Task<ILink> HandoffAsync(ILink current, Endpoint target, CancellationToken ct);
}

/// <summary>A small unbounded MPSC queue of outbound frames usable as an <see cref="IOutboundSource"/> for
/// request/response phases and tests.</summary>
public sealed class ChannelOutbound : IOutboundSource
{
    private readonly Channel<(ushort, ReadOnlyMemory<byte>)> _ch = Channel.CreateUnbounded<(ushort, ReadOnlyMemory<byte>)>();
    /// <summary>Queue a frame.</summary>
    public void Post(ushort opcode, ReadOnlyMemory<byte> payload) => _ch.Writer.TryWrite((opcode, payload));
    /// <inheritdoc/>
    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _ch.Reader.WaitToReadAsync(ct);
    /// <inheritdoc/>
    public bool TryDequeue(out ushort opcode, out ReadOnlyMemory<byte> payload)
    {
        if (_ch.Reader.TryRead(out var t)) { (opcode, payload) = t; return true; }
        opcode = 0; payload = default; return false;
    }
}
