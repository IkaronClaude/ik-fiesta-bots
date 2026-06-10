using System.Text;
using Fiesta.Bot.Login;
using Fiesta.Bot.Net;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Zone;

/// <summary>
/// The zone phase: connect to the zone endpoint from CHAR_LOGIN_ACK, handshake,
/// and send a from-scratch MAP_LOGIN_REQ (0x1801) — chardata (live WM handle +
/// char name) plus the 49 data-file checksums. The zone replies MAP_LOGINFAIL
/// (0x1804, with nWrongDataFileIndex) on a checksum mismatch, or streams the
/// character's initial state (the [1038] burst) once in zone.
///
/// The caller must keep the WM connection OPEN across this call — the zone
/// validates the incoming player against a live WM session.
/// </summary>
public sealed class ZoneEntry
{
    private static readonly ushort OpMapLoginFail = PacketRegistry.GetOpcode<PROTO_NC_MAP_LOGINFAIL_ACK>();
    // MAP_LOGINCOMPLETE (0x1803): the client's "finished loading — spawn me in
    // world" signal, sent right after the server's MAP_LOGIN_ACK. Without it the
    // character stays in a loading limbo (invisible to others, no broadcasts, GM
    // commands ignored). Bare opcode, empty payload — like the heartbeat ack.
    private static readonly ushort OpMapLoginComplete =
        (ushort)(((int)ProtocolCommand.Map << 10) | (int)MapOpcode.LogincompleteCmd);
    // MAP_LOGIN_ACK (0x1802): the server's ack that ends the post-[1801] chardata
    // burst; the client sends MAP_LOGINCOMPLETE only after seeing it.
    private static readonly ushort OpMapLoginAck =
        (ushort)(((int)ProtocolCommand.Map << 10) | (int)MapOpcode.LoginAck);

    private readonly byte[] _xorTable;
    private readonly Action<string> _log;
    private readonly string[] _checksums; // 49, precomputed from the client data

    public ZoneEntry(byte[] xorTable, Action<string> log, string[] checksums)
    {
        if (checksums.Length != DataFileChecksums.Files.Length)
            throw new ArgumentException($"expected {DataFileChecksums.Files.Length} checksums, got {checksums.Length}");
        _xorTable = xorTable;
        _log = log;
        _checksums = checksums;
    }

    /// <summary>Build a ZoneEntry by computing checksums from a client ressystem dir.</summary>
    public static ZoneEntry FromDataDir(byte[] xorTable, Action<string> log, string ressystemDir)
        => new(xorTable, log, DataFileChecksums.ComputeAll(ressystemDir));

    /// <summary>
    /// Enter the zone. Returns the open zone connection on success (in zone), or
    /// throws ZoneEntryException on MAP_LOGINFAIL / timeout.
    /// </summary>
    public async Task<FiestaClientConnection> EnterAsync(
        FiestaEndpoint zoneEp, ushort wmHandle, string charName, CancellationToken ct)
    {
        var conn = await FiestaClientConnection.ConnectAsync(zoneEp.Host, zoneEp.Port, _xorTable, ct);
        try
        {
            await conn.WaitForHandshakeAsync(ct: ct);
            _log($"[Zone] connected {zoneEp}, handshake seed=0x{conn.Seed:X4}");

            var req = new PROTO_NC_MAP_LOGIN_REQ();
            req.chardata.wldmanhandle = wmHandle;
            FillBytes(req.chardata.charid.n5_name, charName);
            for (var i = 0; i < _checksums.Length; i++)
            {
                req.checksum[i] = new Name8();
                FillBytes(req.checksum[i].n8_name, _checksums[i]); // 32 ASCII hex chars
            }
            await conn.SendAsync(req, ct);
            _log($"[Zone] >> MAP_LOGIN_REQ (0x1801) handle={wmHandle} char='{charName}' (+49 checksums)");

            // After [1801] the server streams the chardata burst and ends it with
            // MAP_LOGIN_ACK [1802]. The real client waits for [1802], THEN sends
            // MAP_LOGINCOMPLETE [1803] to finish spawning into the world. Sending
            // [1803] too early (before [1802]) leaves the char in loading limbo —
            // invisible to others, no broadcasts, GM/chat ignored. So drain the
            // burst until [1802] (or, as a fallback, the deadline) before [1803].
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var sawFrame = false;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(remaining);
                FiestaPacket pkt;
                try { pkt = await conn.ReadPacketAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // deadline
                _log($"[Zone] << 0x{pkt.Opcode:X4} dept={pkt.Department} cmd={pkt.Command} len={pkt.Payload.Length}");

                if (pkt.Opcode == OpMapLoginFail)
                {
                    var f = pkt.ReadBody<PROTO_NC_MAP_LOGINFAIL_ACK>();
                    var file = f.nWrongDataFileIndex < DataFileChecksums.Files.Length
                        ? DataFileChecksums.Files[f.nWrongDataFileIndex] + ".shn"
                        : "?";
                    throw new ZoneEntryException(
                        $"MAP_LOGINFAIL err={f.err} wrongDataFileIndex={f.nWrongDataFileIndex} ({file})");
                }

                sawFrame = true;
                if (pkt.Opcode == OpMapLoginAck) // [1802] — the login ack ending the burst
                    return await CompleteLoginAsync(conn, "MAP_LOGIN_ACK", ct);
                // else: a chardata burst frame ([1038] etc.) — keep draining.
            }

            // Fallback: we saw the burst but no explicit [1802] before the deadline.
            // Still complete the login so we spawn rather than hang.
            if (sawFrame)
                return await CompleteLoginAsync(conn, "burst (no explicit [1802])", ct);
            throw new ZoneEntryException("Zone phase timed out with no MAP_LOGINFAIL and no zone traffic");
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Send MAP_LOGINCOMPLETE [1803] to finish spawning into the world,
    /// then hand back the open connection (now fully in zone).</summary>
    private async Task<FiestaClientConnection> CompleteLoginAsync(
        FiestaClientConnection conn, string via, CancellationToken ct)
    {
        await conn.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), ct);
        _log($"[Zone] *** IN ZONE ({via}) >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) ***");
        return conn;
    }

    private static void FillBytes(byte[] dst, string s)
    {
        Array.Clear(dst);
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, dst, Math.Min(bytes.Length, dst.Length));
    }
}

public sealed class ZoneEntryException : Exception
{
    public ZoneEntryException(string message) : base(message) { }
}
