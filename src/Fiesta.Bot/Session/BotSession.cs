using Fiesta.Bot.Login;
using Fiesta.Bot.Net;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;

namespace Fiesta.Bot.Session;

/// <summary>
/// Keeps one bot alive in zone: a single read loop that pumps inbound S→C frames,
/// answers the server's keepalive (Misc HEARTBEAT_REQ → HEARTBEAT_ACK), updates
/// per-bot <see cref="State"/>, and fans every frame out to <see cref="PacketReceived"/>
/// so higher layers (buffing, party, instance assist) can react without owning the
/// socket. Sending is delegated to the connection (already serialized), so action
/// code can <see cref="SendAsync(FiestaPacket, CancellationToken)"/> from any task.
///
/// The zone connection is taken over by this session (entered + handshaked by
/// <c>ZoneEntry</c>) and disposed when the loop ends.
/// </summary>
public sealed class BotSession : IAsyncDisposable
{
    // Keepalive opcodes, derived from the protocol enums (dept<<10 | cmd) — never
    // hand-written hex. The server drives heartbeats; the client only answers.
    private static readonly ushort OpHeartbeatReq = Opcode(ProtocolCommand.Misc, MiscOpcode.HeartbeatReq);
    private static readonly ushort OpHeartbeatAck = Opcode(ProtocolCommand.Misc, MiscOpcode.HeartbeatAck);
    // Clean-logout pair the real client sends on "Quit Game" (from Full.pcapng):
    // Char LOGOUTREADY (0x1071) then User cmd 24 (0x0C18, payload 0x00). Without
    // this the server keeps the char "online" briefly and a quick relog is kicked.
    private static readonly ushort OpLogoutReady = (ushort)(((int)ProtocolCommand.Char << 10) | 113);
    private static readonly ushort OpQuitGame = (ushort)(((int)ProtocolCommand.User << 10) | 24);

    private readonly FiestaClientConnection _conn;
    private readonly Action<string> _log;
    private readonly string _tag;
    private readonly bool _logInbound;

    public BotSession(FiestaClientConnection conn, string charName, ushort wmHandle,
        FiestaEndpoint zone, Action<string> log, string linkTag = "zone", bool logInbound = false)
    {
        _conn = conn;
        _log = log;
        _tag = linkTag;
        _logInbound = logInbound;
        State = new BotSessionState(charName, wmHandle, zone);
    }

    public BotSessionState State { get; }

    /// <summary>Raised for every inbound frame after built-in keepalive handling.
    /// Handlers must not block the loop — offload heavy work.</summary>
    public event Action<FiestaPacket>? PacketReceived;

    /// <summary>Send a C→S frame (enciphered + serialized by the connection).</summary>
    public Task SendAsync(FiestaPacket packet, CancellationToken ct = default)
        => _conn.SendAsync(packet, ct);

    /// <summary>Send the client's clean-logout sequence so the server drops the
    /// character immediately (avoids a duplicate-login kick on the next login).
    /// Best-effort — never throws.</summary>
    public async Task LogoutAsync(bool logoutReady = true, CancellationToken ct = default)
    {
        try
        {
            // Zone link sends LOGOUTREADY first; both links send the quit (User cmd 24).
            if (logoutReady)
                await _conn.SendAsync(new FiestaPacket(OpLogoutReady, ReadOnlyMemory<byte>.Empty), ct);
            await _conn.SendAsync(new FiestaPacket(OpQuitGame, new byte[] { 0x00 }), ct);
            _log($"[Session:{State.CharName}] >> clean logout ({(logoutReady ? "LOGOUTREADY + quit" : "quit")})");
        }
        catch (Exception ex) { _log($"[Session:{State.CharName}] logout send failed: {ex.Message}"); }
    }

    /// <summary>Send a typed C→S body.</summary>
    public Task SendAsync<T>(T body, CancellationToken ct = default) where T : IFiestaPacketBody
        => _conn.SendAsync(body, ct);

    /// <summary>
    /// Pump inbound frames until the peer closes, an error occurs, or
    /// <paramref name="ct"/> is cancelled. Returns when the session ends; the
    /// reason is recorded in <see cref="State"/>. Does not throw on a normal
    /// disconnect / cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log($"[Session:{State.CharName}] read loop started ({State.Zone})");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pkt = await _conn.ReadPacketAsync(ct);
                State.RecordInbound(pkt.Opcode);

                if (_logInbound)
                    _log($"[{_tag}:{State.CharName}] << 0x{pkt.Opcode:X4} d={pkt.Department} c={pkt.Command} " +
                         $"len={pkt.Payload.Length}{HexPreview(pkt.Payload.Span)}");

                if (pkt.Opcode == OpHeartbeatReq)
                {
                    // Bare-opcode reply, empty payload — matches the real client.
                    await _conn.SendAsync(new FiestaPacket(OpHeartbeatAck, ReadOnlyMemory<byte>.Empty), ct);
                    State.RecordHeartbeat();
                    continue;
                }

                try { PacketReceived?.Invoke(pkt); }
                catch (Exception ex) { _log($"[Session:{State.CharName}] handler error on 0x{pkt.Opcode:X4}: {ex.Message}"); }
            }
            Stop("cancelled");
        }
        catch (OperationCanceledException) { Stop("cancelled"); }
        catch (EndOfStreamException) { Stop("peer closed"); }
        catch (Exception ex) { Stop($"{ex.GetType().Name}: {ex.Message}"); }
    }

    private void Stop(string reason)
    {
        if (!State.Connected) return;
        State.Connected = false;
        State.DisconnectedAtUtc = DateTime.UtcNow;
        State.DisconnectReason = reason;
        _log($"[Session:{State.CharName}] ended ({reason}) — uptime {State.Uptime.TotalSeconds:F0}s, " +
             $"{State.InboundCount} frames, {State.HeartbeatCount} heartbeats");
    }

    public ValueTask DisposeAsync()
    {
        _conn.Dispose();
        return ValueTask.CompletedTask;
    }

    private static ushort Opcode(ProtocolCommand dept, MiscOpcode cmd)
        => (ushort)(((int)dept << 10) | ((int)cmd & 0x3FF));

    // First up-to-48 payload bytes as hex, for inbound introspection.
    private static string HexPreview(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return "";
        var n = Math.Min(payload.Length, 48);
        var sb = new System.Text.StringBuilder(" [");
        for (var i = 0; i < n; i++) sb.Append(payload[i].ToString("X2"));
        if (payload.Length > n) sb.Append('…');
        return sb.Append(']').ToString();
    }
}
