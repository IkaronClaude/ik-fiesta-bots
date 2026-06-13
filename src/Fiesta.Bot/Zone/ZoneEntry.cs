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
    public async Task<ZoneEntryResult> EnterAsync(
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
                {
                    // The spawn position is PROTO_NC_CHAR_MAPLOGIN_ACK.logincoord — the
                    // final SHINE_XY (two u32 LE) of the fixed 242-byte body. Parsing the
                    // tail is robust to the big param sub-struct in between. Verified vs
                    // the first MoverunCmd's from-coord (Portals.pcapng).
                    uint? sx = null, sy = null;
                    var span = pkt.Payload.Span;
                    // PROTO_NC_CHAR_MAPLOGIN_ACK.charhandle is the FIRST u16 — the bot's
                    // own in-zone handle, needed to self-target (e.g. self-heal).
                    ushort? charHandle = span.Length >= 2 ? (ushort)(span[0] | (span[1] << 8)) : null;
                    if (span.Length >= 8)
                    {
                        var tail = span[^8..];
                        sx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail);
                        sy = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
                        _log($"[Zone] self handle={charHandle} spawn=({sx},{sy})");
                    }
                    // The body's middle is CHAR_PARAMETER_DATA (232 B) starting after the
                    // charhandle u16, so MaxHp/MaxSp are unsigned longs at param offsets
                    // 144/148 → body offsets 146/150 (PDB-extracted layout). Pull them so
                    // scripts can gate on a fraction of max (HP-stone when low). Current
                    // HP/SP arrive separately via 0x240E/0x240F once in-world.
                    uint? maxHp = null, maxSp = null;
                    if (span.Length >= 154)
                    {
                        maxHp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(146, 4));
                        maxSp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(150, 4));
                        _log($"[Zone] maxHp={maxHp} maxSp={maxSp}");
                    }
                    return await CompleteLoginAsync(conn, "MAP_LOGIN_ACK", sx, sy, charHandle, maxHp, maxSp, ct);
                }
                // else: a chardata burst frame ([1038] etc.) — keep draining.
            }

            // Fallback: we saw the burst but no explicit [1802] before the deadline.
            // Still complete the login so we spawn rather than hang (position unknown).
            if (sawFrame)
                return await CompleteLoginAsync(conn, "burst (no explicit [1802])", null, null, null, null, null, ct);
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
    private async Task<ZoneEntryResult> CompleteLoginAsync(
        FiestaClientConnection conn, string via, uint? spawnX, uint? spawnY, ushort? charHandle,
        uint? maxHp, uint? maxSp, CancellationToken ct)
    {
        await conn.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), ct);
        _log($"[Zone] *** IN ZONE ({via}) >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) ***");
        return new ZoneEntryResult(conn, spawnX, spawnY, charHandle, maxHp, maxSp);
    }

    private static void FillBytes(byte[] dst, string s)
    {
        Array.Clear(dst);
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, dst, Math.Min(bytes.Length, dst.Length));
    }
}

/// <summary>Result of a successful zone entry: the open connection, the char's
/// spawn position, its in-zone <see cref="CharHandle"/> (self handle), and its
/// <see cref="MaxHp"/>/<see cref="MaxSp"/> — all decoded from the [1802] login ack
/// (null if it wasn't seen). Current HP/SP arrive later via HPCHANGE/SPCHANGE.</summary>
public sealed record ZoneEntryResult(
    FiestaClientConnection Conn, uint? SpawnX, uint? SpawnY, ushort? CharHandle, uint? MaxHp = null, uint? MaxSp = null);

public sealed class ZoneEntryException : Exception
{
    public ZoneEntryException(string message) : base(message) { }
}
