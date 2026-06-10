using System.Text;
using Fiesta.Bot.Net;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Login;

/// <summary>
/// Drives the real client login handshake from typed packets (no capture
/// replay): Login → WorldManager → (zone endpoint), with optional in-band
/// character creation. Every step is built from FiestaLib structs and every
/// inbound frame is logged by opcode so the exact server ordering can be
/// confirmed live (REQ→ACK), the same way the OPTool endpoints were verified.
///
/// All opcodes are resolved through <see cref="PacketRegistry.GetOpcode{T}"/> —
/// i.e. derived from each struct's [FiestaOpcode(department, command)] (the
/// 6-bit dept | 10-bit cmd encoding) — never hand-written hex.
/// </summary>
public sealed class LoginChain
{
    // Opcodes we react to, resolved from FiestaLib's struct registry.
    private static readonly ushort OpLoginFailAck        = PacketRegistry.GetOpcode<PROTO_NC_USER_LOGINFAIL_ACK>();
    private static readonly ushort OpLoginAck            = PacketRegistry.GetOpcode<PROTO_NC_USER_LOGIN_ACK>();
    private static readonly ushort OpWorldSelectAck      = PacketRegistry.GetOpcode<PROTO_NC_USER_WORLDSELECT_ACK>();
    private static readonly ushort OpLoginWorldAck       = PacketRegistry.GetOpcode<PROTO_NC_USER_LOGINWORLD_ACK>();
    private static readonly ushort OpLoginWorldFail      = PacketRegistry.GetOpcode<PROTO_NC_USER_LOGINWORLDFAIL_ACK>();
    private static readonly ushort OpCharLoginAck        = PacketRegistry.GetOpcode<PROTO_NC_CHAR_LOGIN_ACK>();
    private static readonly ushort OpCharLoginFail       = PacketRegistry.GetOpcode<PROTO_NC_CHAR_LOGINFAIL_ACK>();
    private static readonly ushort OpTutorialPopup       = PacketRegistry.GetOpcode<PROTO_NC_CHAR_TUTORIAL_POPUP_REQ>();
    private static readonly ushort OpAvatarCreateSucc    = PacketRegistry.GetOpcode<PROTO_NC_AVATAR_CREATESUCC_ACK>();
    private static readonly ushort OpAvatarCreateFail    = PacketRegistry.GetOpcode<PROTO_NC_AVATAR_CREATEFAIL_ACK>();
    private static readonly ushort OpAvatarCreateDataFail = PacketRegistry.GetOpcode<PROTO_NC_AVATAR_CREATEDATAFAIL_ACK>();

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
    /// Login phase: connect, handshake, version check + US_LOGIN_REQ, select
    /// world, return the OTP + advertised WM endpoint. Socket closed on return.
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
        _log($"[Login] >> VERSION_CHECK_REQ ver='{_profile.VersionKey}'");

        // 2) Login: user + MD5 password + spawnapps build tag.
        var req = new PROTO_NC_USER_US_LOGIN_REQ();
        FillSBytes(req.sUserName, creds.Username);
        FillSBytes(req.sPassword, creds.PasswordMd5);
        FillBytes(req.spawnapps.n5_name, _profile.SpawnAppsTag);
        await conn.SendAsync(req, ct);
        _log($"[Login] >> US_LOGIN_REQ user='{creds.Username}' spawnapps='{_profile.SpawnAppsTag}'");

        // 3) XTrap anti-cheat key + world-status probe (the real client sends
        //    both here; harmless if the server stubs them).
        await conn.SendAsync(new PROTO_NC_USER_XTRAP_REQ
        {
            XTrapClientKeyLength = (byte)_profile.XtrapKey.Length,
            XTrapClientKey = _profile.XtrapKey,
        }, ct);
        await conn.SendAsync(new PROTO_NC_USER_WORLD_STATUS_REQ(), ct);
        _log("[Login] >> XTRAP_REQ + WORLD_STATUS_REQ");

        var worldSelected = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var pkt = await ReadWithTimeout(conn, deadline, ct);
            Trace("Login", pkt);

            if (pkt.Opcode == OpLoginFailAck)
            {
                var fail = pkt.ReadBody<PROTO_NC_USER_LOGINFAIL_ACK>();
                throw new LoginChainException($"LOGINFAIL_ACK error={fail.error}");
            }
            if (pkt.Opcode == OpLoginAck && !worldSelected)
            {
                await conn.SendAsync(new PROTO_NC_USER_WORLDSELECT_REQ { worldno = worldNo }, ct);
                worldSelected = true;
                _log($"[Login] >> WORLDSELECT_REQ worldno={worldNo}");
            }
            else if (pkt.Opcode == OpWorldSelectAck)
            {
                var ack = pkt.ReadBody<PROTO_NC_USER_WORLDSELECT_ACK>();
                var otp = OtpFromValidateNew(ack.validate_new);
                var ip = AsciiZ(ack.ip.n4_name);
                var wm = new FiestaEndpoint(string.IsNullOrEmpty(ip) ? loginEp.Host : ip, ack.port);
                _log($"[Login] << WORLDSELECT_ACK status={ack.worldstatus} wm={wm} otp={Convert.ToHexString(otp.AsSpan(0, 8))}…");
                return new LoginPhaseResult(otp, wm, ack.worldstatus);
            }
            // Other inbound frames (version ack, xtrap ack, world status) are
            // just logged; add a reply here if one turns out to gate login.
        }
        throw new LoginChainException("Login phase timed out before WORLDSELECT_ACK");
    }

    /// <summary>
    /// WM phase: connect, LOGINWORLD_REQ (OTP echoed), read the avatar list,
    /// optionally create a character, decline the newbie tutorial, CHAR_LOGIN a
    /// slot, and return the zone endpoint + live WM handle. The WM connection is
    /// returned OPEN — the zone validates the incoming player against a live WM
    /// session, so the caller must keep it open until in-zone, then dispose it.
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
            _log("[WM] >> LOGINWORLD_REQ + OTP");

            var avatars = new List<AvatarSummary>();
            ushort wmHandle = 0;
            AvatarSummary? selected = null;
            var charLoginSent = false;

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var pkt = await ReadWithTimeout(conn, deadline, ct);
                Trace("WM", pkt);

                if (pkt.Opcode == OpLoginWorldFail)
                {
                    var fail = pkt.ReadBody<PROTO_NC_USER_LOGINWORLDFAIL_ACK>();
                    throw new LoginChainException($"LOGINWORLDFAIL error={fail.errorcode.err}");
                }
                if (pkt.Opcode == OpCharLoginFail)
                {
                    var fail = pkt.ReadBody<PROTO_NC_CHAR_LOGINFAIL_ACK>();
                    throw new LoginChainException($"CHAR_LOGINFAIL err={fail.err}");
                }
                if (pkt.Opcode == OpLoginWorldAck)
                {
                    var ack = pkt.ReadBody<PROTO_NC_USER_LOGINWORLD_ACK>();
                    wmHandle = ack.worldmanager;
                    foreach (var a in ack.avatar)
                        avatars.Add(new AvatarSummary(a.chrregnum, AsciiZ(a.name.n5_name), a.slot, a.level));
                    _log($"[WM] << LOGINWORLD_ACK handle={wmHandle} numavatars={avatars.Count}: " +
                         string.Join(", ", avatars.Select(a => $"'{a.Name}'(slot {a.Slot})")));

                    selected = selectSlot is { } s
                        ? avatars.FirstOrDefault(a => a.Slot == s)
                        : avatars.FirstOrDefault();

                    // First-class character creation: if there's nothing to enter
                    // with and a spec was given, create one in-band.
                    if (selected is null && createIfMissing is not null)
                    {
                        selected = await CreateAvatarAsync(conn, createIfMissing, ct);
                        avatars = new List<AvatarSummary>(avatars) { selected };
                    }

                    if (selected is not null && !charLoginSent)
                    {
                        await conn.SendAsync(new PROTO_NC_CHAR_LOGIN_REQ { slot = selected.Slot }, ct);
                        charLoginSent = true;
                        _log($"[WM] >> CHAR_LOGIN_REQ slot={selected.Slot} char='{selected.Name}'");
                    }
                    else if (selected is null)
                    {
                        _log("[WM] account has no avatars and no create spec — cannot enter a zone");
                        return (new WmPhaseResult(wmHandle, avatars, null, null), conn);
                    }
                }
                else if (pkt.Opcode == OpTutorialPopup)
                {
                    // Brand-new char drops into the newbie tutorial. Decline it so
                    // the char proceeds to the normal zone handoff. (The decline
                    // takes effect server-side on the NEXT login; the declining
                    // login itself may CHAR_LOGINFAIL — reconnect to enter.)
                    await conn.SendAsync(new PROTO_NC_CHAR_TUTORIAL_POPUP_ACK { bIsSkip = 1 }, ct);
                    _log("[WM] >> TUTORIAL_POPUP_ACK bIsSkip=1 (declined)");
                }
                else if (pkt.Opcode == OpCharLoginAck)
                {
                    var ack = pkt.ReadBody<PROTO_NC_CHAR_LOGIN_ACK>();
                    var ip = AsciiZ(ack.zoneip.n4_name);
                    var zone = new FiestaEndpoint(string.IsNullOrEmpty(ip) ? wmEp.Host : ip, ack.zoneport);
                    _log($"[WM] << CHAR_LOGIN_ACK zone={zone}");
                    return (new WmPhaseResult(wmHandle, avatars, selected, zone), conn);
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
    /// AVATAR_CREATE_REQ → CREATESUCC_ACK / CREATEFAIL.
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
        _log($"[WM] >> AVATAR_CREATE_REQ slot={spec.Slot} name='{spec.Name}' " +
             $"class={spec.Class}({(byte)spec.Class}) gender={spec.Gender}");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var pkt = await ReadWithTimeout(conn, deadline, ct);
            Trace("WM", pkt);
            if (pkt.Opcode == OpAvatarCreateSucc)
            {
                var ok = pkt.ReadBody<PROTO_NC_AVATAR_CREATESUCC_ACK>();
                var a = ok.avatar;
                var sum = new AvatarSummary(a.chrregnum, AsciiZ(a.name.n5_name), a.slot, a.level);
                _log($"[WM] << AVATAR_CREATESUCC_ACK name='{sum.Name}' slot={sum.Slot} level={sum.Level}");
                return sum;
            }
            if (pkt.Opcode == OpAvatarCreateFail)
            {
                var f = pkt.ReadBody<PROTO_NC_AVATAR_CREATEFAIL_ACK>();
                throw new LoginChainException($"AVATAR_CREATEFAIL err={f.err}");
            }
            if (pkt.Opcode == OpAvatarCreateDataFail)
            {
                var f = pkt.ReadBody<PROTO_NC_AVATAR_CREATEDATAFAIL_ACK>();
                throw new LoginChainException($"AVATAR_CREATEDATAFAIL charid='{AsciiZ(f.charid.n5_name)}' err={f.err}");
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
