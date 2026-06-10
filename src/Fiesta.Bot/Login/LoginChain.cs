using System.Text;
using Fiesta.Bot.Net;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Login;

/// <summary>
/// Drives the real client login handshake from typed packets (no capture
/// replay): Login → WorldManager → (zone endpoint). Every step is built from
/// FiestaLib structs and every inbound frame is logged by opcode so the exact
/// server ordering can be confirmed live (REQ→ACK), the same way the OPTool
/// endpoints were verified.
///
/// Opcodes used (ground truth = FiestaLib opcode list):
///   C→S US_LOGIN_REQ      0x0C5A  (cmd 90): sUserName[260] + sPassword[36] + spawnapps
///   S→C LOGIN_ACK         0x0C0A  (cmd 10): world list
///   C→S WORLDSELECT_REQ   0x0C0B  (cmd 11): worldno
///   S→C WORLDSELECT_ACK   0x0C0C  (cmd 12): worldstatus + ip + port + validate_new[64]=OTP
///   S→C LOGINFAIL_ACK     0x0C09  (cmd  9): error
///   C→S LOGINWORLD_REQ    0x0C0F  (cmd 15): user + validate_new[64]=OTP echoed
///   S→C LOGINWORLD_ACK    0x0C14  (cmd 20): wmhandle + avatars
///   S→C LOGINWORLDFAIL    0x0C15  (cmd 21): error
///   C→S CHAR_LOGIN_REQ    0x1001          : slot
///   S→C CHAR_LOGIN_ACK    0x1003          : zone ip + port
/// </summary>
public sealed class LoginChain
{
    private const ushort OpLoginAck       = 0x0C0A;
    private const ushort OpWorldSelectReq = 0x0C0B;
    private const ushort OpWorldSelectAck = 0x0C0C;
    private const ushort OpLoginFailAck   = 0x0C09;
    private const ushort OpLoginWorldAck  = 0x0C14;
    private const ushort OpLoginWorldFail = 0x0C15;
    private const ushort OpCharLoginAck   = 0x1003;

    private readonly byte[] _xorTable;
    private readonly Action<string> _log;
    private readonly ClientProfile _profile;

    public LoginChain(byte[] xorTable, Action<string> log, ClientProfile? profile = null)
    {
        _xorTable = xorTable;
        _log = log;
        _profile = profile ?? ClientProfile.ClientProd2;
    }

    /// <summary>
    /// Login phase: connect, handshake, US_LOGIN_REQ, select world, return the
    /// OTP + advertised WM endpoint. The login socket is closed on return.
    /// </summary>
    public async Task<LoginPhaseResult> RunLoginAsync(
        FiestaEndpoint loginEp, BotCredentials creds, byte worldNo, CancellationToken ct)
    {
        using var conn = await FiestaClientConnection.ConnectAsync(loginEp.Host, loginEp.Port, _xorTable, ct);
        await conn.WaitForHandshakeAsync(ct: ct);
        _log($"[Login] connected {loginEp}, handshake seed=0x{conn.Seed:X4}");

        // 1) Version check FIRST — the server version-gates before login and
        //    drops the socket if it's missing. The 64-byte key is the build
        //    version string (the client leaves the tail as uninitialised stack,
        //    so the server only checks the prefix).
        var ver = new PROTO_NC_USER_CLIENT_VERSION_CHECK_REQ();
        FillSBytes(ver.sVersionKey, _profile.VersionKey);
        await conn.SendAsync(ver, ct);
        _log($"[Login] >> VERSION_CHECK_REQ (0x0C65) ver='{_profile.VersionKey}'");

        // 2) Login: user + MD5 password + spawnapps build tag.
        var req = new PROTO_NC_USER_US_LOGIN_REQ();
        FillSBytes(req.sUserName, creds.Username);
        FillSBytes(req.sPassword, creds.PasswordMd5);
        FillBytes(req.spawnapps.n5_name, _profile.SpawnAppsTag);
        await conn.SendAsync(req, ct);
        _log($"[Login] >> US_LOGIN_REQ (0x0C5A) user='{creds.Username}' spawnapps='{_profile.SpawnAppsTag}'");

        // 3) XTrap anti-cheat key + world-status probe (the real client sends
        //    both here; harmless if the server stubs them).
        await conn.SendAsync(new PROTO_NC_USER_XTRAP_REQ
        {
            XTrapClientKeyLength = (byte)_profile.XtrapKey.Length,
            XTrapClientKey = _profile.XtrapKey,
        }, ct);
        await conn.SendAsync(new PROTO_NC_USER_WORLD_STATUS_REQ(), ct);
        _log("[Login] >> XTRAP_REQ (0x0C04) + WORLD_STATUS_REQ (0x0C1B)");

        var worldSelected = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var pkt = await ReadWithTimeout(conn, deadline, ct);
            Trace("Login", pkt);

            switch (pkt.Opcode)
            {
                case OpLoginFailAck:
                {
                    var fail = pkt.ReadBody<PROTO_NC_USER_LOGINFAIL_ACK>();
                    throw new LoginChainException($"LOGINFAIL_ACK error={fail.error}");
                }
                case OpLoginAck:
                    if (!worldSelected)
                    {
                        await conn.SendAsync(new PROTO_NC_USER_WORLDSELECT_REQ { worldno = worldNo }, ct);
                        worldSelected = true;
                        _log($"[Login] >> WORLDSELECT_REQ (0x0C0B) worldno={worldNo}");
                    }
                    break;
                case OpWorldSelectAck:
                {
                    var ack = pkt.ReadBody<PROTO_NC_USER_WORLDSELECT_ACK>();
                    var otp = OtpFromValidateNew(ack.validate_new);
                    var ip = AsciiZ(ack.ip.n4_name);
                    var wm = new FiestaEndpoint(string.IsNullOrEmpty(ip) ? loginEp.Host : ip, ack.port);
                    _log($"[Login] << WORLDSELECT_ACK status={ack.worldstatus} wm={wm} otp={Convert.ToHexString(otp.AsSpan(0, 8))}…");
                    return new LoginPhaseResult(otp, wm, ack.worldstatus);
                }
                // Other inbound frames (version check, xtrap, etc.) are just
                // logged; if one turns out to gate login we add a reply here.
            }
        }
        throw new LoginChainException("Login phase timed out before WORLDSELECT_ACK");
    }

    /// <summary>
    /// WM phase: connect to the WorldManager, send LOGINWORLD_REQ with the OTP,
    /// read the avatar list, optionally CHAR_LOGIN_REQ a slot, and return the
    /// zone endpoint + live WM handle. The WM connection is returned OPEN — the
    /// zone validates the incoming player against a live WM session, so the
    /// caller must keep it open until in-zone, then dispose it.
    /// </summary>
    public async Task<(WmPhaseResult Result, FiestaClientConnection WmConn)> RunWmAsync(
        FiestaEndpoint wmEp, BotCredentials creds, byte[] otp, byte? selectSlot,
        CharacterSpec? createIfMissing, CancellationToken ct)
    {
        var conn = await FiestaClientConnection.ConnectAsync(wmEp.Host, wmEp.Port, _xorTable, ct);
        try
        {
            await conn.WaitForHandshakeAsync(ct: ct);
            _log($"[WM] connected {wmEp}, handshake seed=0x{conn.Seed:X4}");

            var req = new PROTO_NC_USER_LOGINWORLD_REQ();
            FillBytes(req.user.n256_name, creds.Username);
            ValidateNewFromOtp(req.validate_new, otp);
            await conn.SendAsync(req, ct);
            _log("[WM] >> LOGINWORLD_REQ (0x0C0F) + OTP");

            var avatars = new List<AvatarSummary>();
            ushort wmHandle = 0;
            AvatarSummary? selected = null;
            FiestaEndpoint? zone = null;
            var charLoginSent = false;

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var pkt = await ReadWithTimeout(conn, deadline, ct);
                Trace("WM", pkt);

                switch (pkt.Opcode)
                {
                    case OpLoginWorldFail:
                    {
                        var fail = pkt.ReadBody<PROTO_NC_USER_LOGINWORLDFAIL_ACK>();
                        throw new LoginChainException($"LOGINWORLDFAIL error={fail.errorcode.err}");
                    }
                    case OpLoginWorldAck:
                    {
                        var ack = pkt.ReadBody<PROTO_NC_USER_LOGINWORLD_ACK>();
                        wmHandle = ack.worldmanager;
                        foreach (var a in ack.avatar)
                            avatars.Add(new AvatarSummary(a.chrregnum, AsciiZ(a.name.n5_name), a.slot, a.level));
                        _log($"[WM] << LOGINWORLD_ACK handle={wmHandle} numavatars={avatars.Count}: " +
                             string.Join(", ", avatars.Select(a => $"'{a.Name}'(slot {a.Slot})")));

                        // Pick the avatar to enter with.
                        selected = selectSlot is { } s
                            ? avatars.FirstOrDefault(a => a.Slot == s)
                            : avatars.FirstOrDefault();

                        // First-class character creation: if there's no avatar to
                        // enter with and a spec was given, create one in-band.
                        if (selected is null && createIfMissing is not null)
                        {
                            selected = await CreateAvatarAsync(conn, createIfMissing, ct);
                            avatars = new List<AvatarSummary>(avatars) { selected };
                        }

                        if (selected is not null && !charLoginSent)
                        {
                            await conn.SendAsync(new FiestaPacket(0x1001, new byte[] { selected.Slot }), ct);
                            charLoginSent = true;
                            _log($"[WM] >> CHAR_LOGIN_REQ (0x1001) slot={selected.Slot} char='{selected.Name}'");
                        }
                        else if (selected is null)
                        {
                            _log("[WM] account has no avatars and no create spec — cannot enter a zone");
                            return (new WmPhaseResult(wmHandle, avatars, null, null), conn);
                        }
                        break;
                    }
                    case OpCharLoginAck:
                    {
                        var span = pkt.Payload.Span;
                        var ip = AsciiZ(span[..Math.Min(16, span.Length)].ToArray());
                        var port = span.Length >= 18 ? (ushort)(span[16] | (span[17] << 8)) : (ushort)0;
                        zone = new FiestaEndpoint(string.IsNullOrEmpty(ip) ? wmEp.Host : ip, port);
                        _log($"[WM] << CHAR_LOGIN_ACK (0x1003) zone={zone}");
                        return (new WmPhaseResult(wmHandle, avatars, selected, zone), conn);
                    }
                }
            }
            throw new LoginChainException("WM phase timed out before CHAR_LOGIN_ACK");
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Create a character in-band on an open WM connection (char-select screen):
    /// AVATAR_CREATE_REQ (0x1401) → CREATESUCC_ACK (0x1406) / CREATEFAIL (0x1404).
    /// </summary>
    public async Task<AvatarSummary> CreateAvatarAsync(
        FiestaClientConnection conn, CharacterSpec spec, CancellationToken ct)
    {
        var req = new PROTO_NC_AVATAR_CREATE_REQ { slotnum = spec.Slot };
        FillBytes(req.name.n5_name, spec.Name);
        req.char_shape.race = spec.Race;
        req.char_shape.chrclass = (uint)spec.Class;
        req.char_shape.gender = spec.Gender;
        req.char_shape.hairtype = spec.HairType;
        req.char_shape.haircolor = spec.HairColor;
        req.char_shape.faceshape = spec.FaceShape;
        await conn.SendAsync(req, ct);
        _log($"[WM] >> AVATAR_CREATE_REQ (0x1401) slot={spec.Slot} name='{spec.Name}' " +
             $"class={spec.Class}({(byte)spec.Class}) gender={spec.Gender}");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var pkt = await ReadWithTimeout(conn, deadline, ct);
            Trace("WM", pkt);
            switch (pkt.Opcode)
            {
                case 0x1406: // AVATAR_CREATESUCC_ACK
                {
                    var ok = pkt.ReadBody<PROTO_NC_AVATAR_CREATESUCC_ACK>();
                    var a = ok.avatar;
                    var sum = new AvatarSummary(a.chrregnum, AsciiZ(a.name.n5_name), a.slot, a.level);
                    _log($"[WM] << AVATAR_CREATESUCC_ACK name='{sum.Name}' slot={sum.Slot} level={sum.Level}");
                    return sum;
                }
                case 0x1404: // AVATAR_CREATEFAIL_ACK
                {
                    var f = pkt.ReadBody<PROTO_NC_AVATAR_CREATEFAIL_ACK>();
                    throw new LoginChainException($"AVATAR_CREATEFAIL err={f.err}");
                }
                case 0x1403: // AVATAR_CREATEDATAFAIL_ACK
                {
                    var f = pkt.ReadBody<PROTO_NC_AVATAR_CREATEDATAFAIL_ACK>();
                    throw new LoginChainException(
                        $"AVATAR_CREATEDATAFAIL charid='{AsciiZ(f.charid.n5_name)}' err={f.err}");
                }
            }
        }
        throw new LoginChainException("AVATAR_CREATE timed out before an ACK");
    }

    // ---- helpers ----

    private void Trace(string tag, FiestaPacket pkt)
        => _log($"[{tag}] << 0x{pkt.Opcode:X4} dept={pkt.Department} cmd={pkt.Command} len={pkt.Payload.Length}");

    private static async ValueTask<FiestaPacket> ReadWithTimeout(
        FiestaClientConnection conn, DateTime deadline, CancellationToken ct)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new LoginChainException("read deadline exceeded");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(remaining);
        return await conn.ReadPacketAsync(cts.Token);
    }

    private static byte[] OtpFromValidateNew(ushort[] validateNew)
    {
        var otp = new byte[validateNew.Length * 2];
        Buffer.BlockCopy(validateNew, 0, otp, 0, otp.Length);
        return otp;
    }

    private static void ValidateNewFromOtp(ushort[] validateNew, byte[] otp)
        => Buffer.BlockCopy(otp, 0, validateNew, 0, Math.Min(otp.Length, validateNew.Length * 2));

    private static void FillSBytes(sbyte[] dst, string s)
    {
        Array.Clear(dst);
        var bytes = Encoding.ASCII.GetBytes(s);
        for (var i = 0; i < bytes.Length && i < dst.Length; i++) dst[i] = (sbyte)bytes[i];
    }

    private static void FillBytes(byte[] dst, string s)
    {
        Array.Clear(dst);
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, dst, Math.Min(bytes.Length, dst.Length));
    }

    private static string AsciiZ(byte[] buf)
    {
        var n = Array.IndexOf(buf, (byte)0);
        if (n < 0) n = buf.Length;
        return Encoding.ASCII.GetString(buf, 0, n);
    }
}
