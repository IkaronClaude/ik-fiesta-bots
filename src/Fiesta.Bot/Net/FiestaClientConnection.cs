using System.Net.Sockets;
using FiestaLibReloaded.Networking;

namespace Fiesta.Bot.Net;

/// <summary>
/// A synthetic Fiesta *client* connection (the c2s side a real game client
/// speaks). The protocol is asymmetric:
///   • S→C frames are plaintext — we read them without transforming.
///   • C→S frames have their (opcode+payload) XOR'd with the BYO table, the
///     cipher position starting at the handshake <c>seed</c> the server sends.
///
/// This is why we don't reuse FiestaLib's <c>FiestaConnection</c> (which
/// transforms both directions): here only the send path is enciphered. We still
/// reuse <see cref="FiestaPacket"/> and all the typed struct bodies.
///
/// Framing (both directions): length prefix is 1 byte (1..255) or, for ≥256,
/// <c>0x00</c> + little-endian u16. Body = opcode (LE u16) + payload.
/// </summary>
public sealed class FiestaClientConnection : IDisposable
{
    /// <summary>Handshake frame opcode: bytes 0x07 0x08 (LE), 2-byte seed payload.</summary>
    public const ushort HandshakeOpcode = 0x0807;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _xorTable;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private XorStreamCipher? _sendCipher;
    private bool _disposed;

    private FiestaClientConnection(TcpClient tcp, byte[] xorTable)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _xorTable = xorTable;
    }

    /// <summary>
    /// Optional observer fired for every frame on this connection, in both directions,
    /// with the <b>plaintext</b> (XOR-decoded) opcode + payload — outbound is captured
    /// BEFORE the send cipher transforms it, inbound is plaintext already. Set at runtime
    /// to tap traffic (e.g. a packet log); null = no overhead. Args: (outbound, opcode, payload).
    /// Must not throw or block — it runs inline on the read/send path.
    /// </summary>
    public Action<bool, ushort, ReadOnlyMemory<byte>>? PacketTap { get; set; }

    public bool HandshakeComplete => _sendCipher is not null;

    /// <summary>The seed the server sent in its handshake frame (0 until handshaked).</summary>
    public int Seed { get; private set; }

    public static async Task<FiestaClientConnection> ConnectAsync(
        string host, int port, byte[] xorTable, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        return new FiestaClientConnection(tcp, xorTable);
    }

    /// <summary>
    /// Read S→C frames until the server's handshake (<c>[07 08 seedLo seedHi]</c>)
    /// arrives, then arm the send cipher at that seed. Any non-handshake frames
    /// seen first are returned in <paramref name="preamble"/> (normally none).
    /// </summary>
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

    /// <summary>Read one plaintext S→C packet (blocking until a full frame arrives).</summary>
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
        // S→C is plaintext: no cipher transform.
        var opcode = (ushort)(frame[0] | (frame[1] << 8));
        var payload = new byte[frameLen - 2];
        if (payload.Length > 0)
            Buffer.BlockCopy(frame, 2, payload, 0, payload.Length);
        PacketTap?.Invoke(false, opcode, payload);
        return new FiestaPacket(opcode, payload);
    }

    /// <summary>Encipher and send one C→S packet. Serialized (cipher is stateful).</summary>
    public async Task SendAsync(FiestaPacket packet, CancellationToken ct = default)
    {
        if (_sendCipher is null)
            throw new InvalidOperationException("Send before handshake — call WaitForHandshakeAsync first");

        // Tap BEFORE the cipher transform so observers see plaintext (the c2s wire is enciphered).
        PacketTap?.Invoke(true, packet.Opcode, packet.Payload);

        var bodyLen = 2 + packet.Payload.Length;
        var body = new byte[bodyLen];
        body[0] = (byte)(packet.Opcode & 0xFF);
        body[1] = (byte)(packet.Opcode >> 8);
        packet.Payload.Span.CopyTo(body.AsSpan(2));

        await _sendLock.WaitAsync(ct);
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
            await _stream.WriteAsync(wire, ct);
            await _stream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Convenience: serialize a typed body and send it.</summary>
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
        _stream.Dispose();
        _tcp.Dispose();
        _sendLock.Dispose();
    }
}
