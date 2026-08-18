using System.Net.Sockets;
using FiestaLibReloaded.Networking;

namespace Fiesta.Bot.Net;

/// <summary>A synthetic Fiesta *client* connection (the c2s side a real game client speaks)</summary>
public sealed class FiestaClientConnection : IDisposable
{
    /// <summary>Handshake frame opcode: bytes 0x07 0x08 (LE), 2-byte seed payload</summary>
    public const ushort HandshakeOpcode = 0x0807;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _xorTable;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Diagnostic sink for connection-level anomalies that are INVISIBLE ON THE WIRE — sends after dispose, send-lock…</summary>
    public Action<string>? Diag { get; set; }

    // Thresholds. Bot-behaviour timing only, not game facts
    private const int SendLockWaitMs = 5_000;      // give up waiting for the lock (something is stuck)
    private const int SendLockSlowMs = 250;        // warn: we queued behind another sender this long
    private const int SendWriteTimeoutMs = 5_000;  // a write must not outlive this (half-open socket)
    private const int SendWriteSlowMs = 500;       // warn: socket is slow
    private long _lockHeldSince;                   // when the current holder took the lock
    private ushort _lockHolderOpcode;              // what the current holder is sending
    private int _sendersWaiting;                   // queue depth behind the lock

    private XorStreamCipher? _sendCipher;
    private bool _disposed;

    private FiestaClientConnection(TcpClient tcp, byte[] xorTable)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _xorTable = xorTable;
    }

    /// <summary>Optional observer fired for every frame on this connection, in both directions, with the plaintext (XOR-decode…</summary>
    public Action<bool, ushort, ReadOnlyMemory<byte>>? PacketTap { get; set; }

    public bool HandshakeComplete => _sendCipher is not null;

    /// <summary>The seed the server sent in its handshake frame (0 until handshaked)</summary>
    public int Seed { get; private set; }

    public static async Task<FiestaClientConnection> ConnectAsync(
        string host, int port, byte[] xorTable, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        EnableKeepAlive(tcp.Client);
        return new FiestaClientConnection(tcp, xorTable);
    }

    /// <summary>GHOST-FIX (P0, operator 2026-07-28): turn on TCP keepalive so the OS detects a DEAD PEER in bounded time</summary>
    private static void EnableKeepAlive(Socket s)
    {
        try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30); } catch { }      // idle secs before the first probe
        try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10); } catch { }  // secs between probes
        try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); } catch { } // dead after this many unanswered probes
    }

    /// <summary>Read S→C frames until the server's handshake ( [07 08 seedLo seedHi] ) arrives, then arm the send cipher at th…</summary>
    public async Task WaitForHandshakeAsync(
        List<FiestaPacket>? preamble = null, CancellationToken ct = default)
    {
        while (true)
        {
            var pkt = await ReadPacketAsync(ct);
            if (pkt.Opcode == HandshakeOpcode && pkt.Payload.Length >= 2)
            {
                var p = pkt.Payload.Span;
                Seed = p[0] | (p[1] << 8);
                _sendCipher = new XorStreamCipher(_xorTable, Seed);
                return;
            }
            preamble?.Add(pkt);
        }
    }

    /// <summary>Read one plaintext S→C packet (blocking until a full frame arrives)</summary>
    public async ValueTask<FiestaPacket> ReadPacketAsync(CancellationToken ct = default)
    {
        var first = await ReadByteAsync(ct);
        int frameLen;
        if (first != 0x00)
        {
            frameLen = first;
        }
        else
        {
            var lo = await ReadByteAsync(ct);
            var hi = await ReadByteAsync(ct);
            frameLen = (hi << 8) | lo;
        }
        if (frameLen < 2)
            throw new InvalidDataException($"Frame too short: {frameLen}");

        var frame = new byte[frameLen];
        await ReadExactAsync(frame, ct);
        // S→C is plaintext: no cipher transform
        var opcode = (ushort)(frame[0] | (frame[1] << 8));
        var payload = new byte[frameLen - 2];
        if (payload.Length > 0)
            Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        PacketTap?.Invoke(false, opcode, payload);
        return new FiestaPacket(opcode, payload);
    }

    /// <summary>Encipher and send one C→S packet</summary>
    public async Task SendAsync(FiestaPacket packet, CancellationToken ct = default)
    {
        if (_sendCipher is null)
            throw new InvalidOperationException("Send before handshake — call WaitForHandshakeAsync first");

        // Tap BEFORE the cipher transform so observers see plaintext (the c2s wire is enciphered)
        PacketTap?.Invoke(true, packet.Opcode, packet.Payload);

        var bodyLen = 2 + packet.Payload.Length;
        var body = new byte[bodyLen];
        body[0] = (byte)(packet.Opcode & 0xFF);
        body[1] = (byte)(packet.Opcode >> 8);
        packet.Payload.Span.CopyTo(body.AsSpan(2));

        // ===== SEND INSTRUMENTATION (operator 2026-08-04: "add as much logging as possible to catch it red handed")
        if (_disposed)
        {
            Diag?.Invoke($"[conn] ⛔ SEND AFTER DISPOSE op=0x{packet.Opcode:X4} — caller is using a dead connection");
            throw new ObjectDisposedException(nameof(FiestaClientConnection), $"send op=0x{packet.Opcode:X4} after dispose");
        }

        var waitStart = Environment.TickCount64;
        var queued = Interlocked.Increment(ref _sendersWaiting);
        try
        {
            if (!await _sendLock.WaitAsync(SendLockWaitMs, ct))
            {
                Diag?.Invoke($"[conn] ⛔ SEND LOCK TIMEOUT op=0x{packet.Opcode:X4} after {SendLockWaitMs}ms " +
                             $"— holder op=0x{_lockHolderOpcode:X4} has held it {Environment.TickCount64 - _lockHeldSince}ms, {queued} sender(s) queued. " +
                             "This is the freeze: a stuck send is wedging every other send.");
                throw new TimeoutException($"send lock timeout op=0x{packet.Opcode:X4}");
            }
        }
        finally { Interlocked.Decrement(ref _sendersWaiting); }

        var waited = Environment.TickCount64 - waitStart;
        if (waited > SendLockSlowMs)
            Diag?.Invoke($"[conn] ⚠ send op=0x{packet.Opcode:X4} WAITED {waited}ms for the send lock (holder was op=0x{_lockHolderOpcode:X4})");
        _lockHeldSince = Environment.TickCount64;
        _lockHolderOpcode = packet.Opcode;
        try
        {
            _sendCipher.Transform(body); // advances cipher position; must be under the lock
            byte[] wire = bodyLen <= 0xFF
                ? new byte[1 + bodyLen]
                : new byte[3 + bodyLen];
            if (bodyLen <= 0xFF)
            {
                wire[0] = (byte)bodyLen;
                Buffer.BlockCopy(body, 0, wire, 1, bodyLen);
            }
            else
            {
                wire[0] = 0x00;
                wire[1] = (byte)(bodyLen & 0xFF);
                wire[2] = (byte)(bodyLen >> 8);
                Buffer.BlockCopy(body, 0, wire, 3, bodyLen);
            }
            // WRITE TIMEOUT — without this a half-open TCP socket blocks here until the OS timeout (minutes) WHILE HOLDING _…
            using var wcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wcts.CancelAfter(SendWriteTimeoutMs);
            var wStart = Environment.TickCount64;
            try
            {
                await _stream.WriteAsync(wire, wcts.Token);
                await _stream.FlushAsync(wcts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Diag?.Invoke($"[conn] ⛔ SEND WRITE TIMEOUT op=0x{packet.Opcode:X4} after {SendWriteTimeoutMs}ms " +
                             "— socket accepted no bytes (half-open / never closed). Treating the link as dead.");
                throw new IOException($"write timeout op=0x{packet.Opcode:X4} (half-open socket)");
            }
            var wms = Environment.TickCount64 - wStart;
            if (wms > SendWriteSlowMs)
                Diag?.Invoke($"[conn] ⚠ send op=0x{packet.Opcode:X4} WRITE took {wms}ms (slow socket / backpressure)");
        }
        finally
        {
            _lockHolderOpcode = 0; _lockHeldSince = 0;
            // Never touch a disposed semaphore — Dispose() no longer disposes it (see Dispose)
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Convenience: serialize a typed body and send it</summary>
    public Task SendAsync<T>(T body, CancellationToken ct = default) where T : IFiestaPacketBody
        => SendAsync(FiestaPacket.Create(body), ct);

    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        var buf = new byte[1];
        var read = await _stream.ReadAsync(buf, ct);
        if (read == 0) throw new EndOfStreamException("Peer closed the connection");
        return buf[0];
    }

    private async ValueTask ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) throw new EndOfStreamException("Peer closed mid-frame");
            total += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lockHolderOpcode != 0 || _sendersWaiting > 0)
            Diag?.Invoke($"[conn] ⚠ DISPOSE while a send is in flight — holder op=0x{_lockHolderOpcode:X4} " +
                         $"held {Environment.TickCount64 - _lockHeldSince}ms, {_sendersWaiting} waiting. " +
                         "Those senders will now fail fast rather than block.");
        _stream.Dispose();
        _tcp.Dispose();
        // Do NOT dispose _sendLock
    }
}
