using System.Text;
using Fiesta.Bot.Login;
using Fiesta.Bot.Net;
using FiestaLibReloaded.Networking;
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

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(remaining);
                var pkt = await conn.ReadPacketAsync(cts.Token);
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

                // Anything else after [1801] means the zone accepted us and is
                // streaming the character's initial state ([1038] burst etc.).
                _log($"[Zone] *** IN ZONE — first post-login frame 0x{pkt.Opcode:X4} ***");
                return conn;
            }
            throw new ZoneEntryException("Zone phase timed out with no MAP_LOGINFAIL and no zone traffic");
        }
        catch
        {
            conn.Dispose();
            throw;
        }
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
