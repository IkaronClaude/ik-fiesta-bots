using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Fiesta.Bot.Net;

namespace Fiesta.Bot.Protocol;

/// <summary>Which packet set a bot speaks. Selected per bot (spawn request / fleet record), default from the host
/// configuration (<c>FIESTA_WIRE</c>).</summary>
public enum WireKind : byte
{
    /// <summary>The 2016 client's packet set (the original 2016 Login / WorldManager / Zone).</summary>
    Wire2016,
    /// <summary>The 2026 US client's packet set (2026-wire Login / WorldManager / Zone builds).</summary>
    Wire2026,
}

/// <summary>Outbound messages whose opcode or body differs between wire profiles (everything else is shared).</summary>
public enum OutMsg : byte { NormalLogout, AvatarListReq, LogoutReady }

/// <summary>A wire profile: everything that differs between the 2016 and 2026 packet sets, behind one interface, so
/// decoders, the world state and the brain are shared. Differences are of four kinds:
/// <list type="number">
/// <item>the login handshake (<see cref="CreateLogin"/>): different opcodes, bodies and steps up to the zone endpoint;</item>
/// <item>the MAP_LOGIN_REQ checksum list (<see cref="ChecksumFiles"/>);</item>
/// <item>inbound zone packets whose 2026 shape is wider (<see cref="Normalize"/> turns them into the canonical,
///   2016-shaped layout the decoders read; hot packets are read width-aware without a copy);</item>
/// <item>outbound opcodes that moved (<see cref="Opcode"/>) and extra frames the 2026 client sends at fixed
///   moments (<see cref="AfterMapLoginAck"/>).</item>
/// </list></summary>
public abstract class WireProfile
{
    /// <summary>Kind.</summary>
    public abstract WireKind Kind { get; }
    /// <summary>"2016" / "2026".</summary>
    public string Name => Kind == WireKind.Wire2016 ? "2016" : "2026";
    /// <summary>Create the login chain for this packet set.</summary>
    public abstract ILoginChain CreateLogin(byte[] xorTable);
    /// <summary>MAP_LOGIN_REQ checksum slots in order; null = a slot the client sends as 32 '0' characters.</summary>
    public abstract IReadOnlyList<string?> ChecksumFiles { get; }
    /// <summary>Opcode of an outbound message that moved between packet sets.</summary>
    public abstract ushort Opcode(OutMsg msg);
    /// <summary>Width of one BRIEFINFO_REGENMOB record.</summary>
    public abstract int RegenMobWidth { get; }
    /// <summary>Width of one BRIEFINFO_LOGINCHARACTER record.</summary>
    public abstract int LoginCharacterWidth { get; }
    /// <summary>Width of SWING_DAMAGE (16 / 25) and its damage field width (2 / 4).</summary>
    public abstract (int Length, int DamageBytes) SwingDamage { get; }
    /// <summary>Canonicalise a wider inbound packet. Returns the payload itself when the opcode is not translated (or the
    /// frame is not the expected shape, which is then counted in <see cref="Mismatches"/>).</summary>
    public virtual ReadOnlySpan<byte> Normalize(ushort opcode, ReadOnlySpan<byte> payload, ref byte[]? scratch) => payload;
    /// <summary>Frames that arrived in a shape the profile did not expect (opcode -> count).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<ushort, int> Mismatches { get; } = new();
    /// <summary>Frames the client sends right after MAP_LOGIN_ACK, before MAP_LOGINCOMPLETE (zone, then WM lists).</summary>
    public virtual void AfterMapLoginAck(List<(ushort Op, byte[] Body)> zone, List<(ushort Op, byte[] Body)> wm) { }
    /// <summary>Frames the client sends right after MAP_LOGINCOMPLETE.</summary>
    public virtual void AfterLoginComplete(List<(ushort Op, byte[] Body)> zone) { }
    /// <summary>Item class lookup the item-record translation needs (set from client data).</summary>
    public Func<int, int> ItemClassOf { get; set; } = _ => -1;

    /// <summary>Profile for a kind (shared instances; profiles hold no per-bot state except counters).</summary>
    public static WireProfile For(WireKind kind) => kind == WireKind.Wire2016 ? Wire2016Profile.Instance : Wire2026Profile.Instance;
    /// <summary>Parse "2016" / "2026" (default when null/empty: <c>FIESTA_WIRE</c>, else 2016).</summary>
    public static WireKind Parse(string? s)
    {
        s ??= Environment.GetEnvironmentVariable("FIESTA_WIRE");
        return s?.Trim() switch { "2026" or "Wire2026" or "wire2026" => WireKind.Wire2026, _ => WireKind.Wire2016 };
    }

    /// <summary>Build MAP_LOGIN_REQ: {wm handle u16, charid[20]} + 32-char checksums.</summary>
    public byte[] BuildMapLogin(ushort wmHandle, string charName, IReadOnlyList<string> checksums)
    {
        var body = new byte[22 + 32 * checksums.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(body, wmHandle);
        Ascii.Put(body.AsSpan(2, 20), charName);
        for (var i = 0; i < checksums.Count; i++) Ascii.Put(body.AsSpan(22 + 32 * i, 32), checksums[i]);
        return body;
    }

    /// <summary>The checksums for <see cref="ChecksumFiles"/> from a ressystem directory (missing file = '0' slot).</summary>
    public string[] ComputeChecksums(string ressystemDir)
    {
        var files = ChecksumFiles;
        var o = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var path = f is null ? null : Path.Combine(ressystemDir, f + ".shn");
            o[i] = path is not null && File.Exists(path) ? DataChecksum(path) : new string('0', 32);
        }
        return o;
    }

    /// <summary>The zone's data-file checksum: MD5(header[0x24] + data cipher(rest)), lower-case hex.</summary>
    public static string DataChecksum(string shnPath)
    {
        var d = File.ReadAllBytes(shnPath);
        if (d.Length < 0x24) throw new InvalidDataException($"{shnPath}: too short for a .shn header");
        DataCipher(d.AsSpan(0x24));
        return Convert.ToHexString(MD5.HashData(d)).ToLowerInvariant();
    }

    /// <summary>The symmetric data-file cipher (used by .shn files and the checksum).</summary>
    public static void DataCipher(Span<byte> b)
    {
        var n = b.Length;
        var key = (byte)n;
        for (var i = n - 1; i >= 0; i--)
        {
            b[i] ^= key;
            var a = (byte)((i & 0x0F) + 0x55);
            a ^= (byte)(i * 11);
            a ^= key;
            a ^= 0xAA;
            key = a;
        }
    }
}

/// <summary>ASCII fixed-field helpers.</summary>
public static class Ascii
{
    /// <summary>Write a string into a zero-filled fixed field.</summary>
    public static void Put(Span<byte> dst, string s)
    {
        dst.Clear();
        for (var i = 0; i < s.Length && i < dst.Length; i++) dst[i] = (byte)s[i];
    }
    /// <summary>Read a zero-terminated string from a fixed field.</summary>
    public static string Get(ReadOnlySpan<byte> src)
    {
        var n = src.IndexOf((byte)0);
        if (n < 0) n = src.Length;
        return Encoding.Latin1.GetString(src[..n]);
    }
    /// <summary>MD5 hex of a password (the in-game password is the raw MD5, lower-case hex).</summary>
    public static string Md5Hex(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>The 2016 packet set.</summary>
public sealed class Wire2016Profile : WireProfile
{
    /// <summary>Shared instance.</summary>
    public static readonly Wire2016Profile Instance = new();
    /// <inheritdoc/>
    public override WireKind Kind => WireKind.Wire2016;
    /// <inheritdoc/>
    public override ILoginChain CreateLogin(byte[] xorTable) => new Login2016(xorTable);
    /// <summary>The 49 checked tables in the order the 2016 zone reads them.</summary>
    public static readonly string[] Files =
    [
        "AbState", "ActiveSkill", "CharacterTitleData", "ChargedEffect", "ClassName", "Gather", "GradeItemOption",
        "ItemDismantle", "ItemInfo", "MapInfo", "MiniHouse", "MiniHouseFurniture", "MiniHouseObjAni", "MobInfo",
        "PassiveSkill", "Riding", "SubAbState", "UpgradeInfo", "WeaponAttrib", "WeaponTitleData",
        "MiniHouseFurnitureObjEffect", "MiniHouseEndure", "DiceDividind", "ActionViewInfo", "MapLinkPoint",
        "MapWayPoint", "AbStateView", "ActiveSkillView", "CharacterTitleStateView", "EffectViewInfo", "ItemShopView",
        "ItemViewInfo", "MapViewInfo", "MobViewInfo", "NPCViewInfo", "PassiveSkillView", "ProduceView",
        "CollectCardView", "GTIView", "ItemViewEquipTypeInfo", "SingleData", "MarketSearchInfo", "ItemMoney",
        "PupMain", "ChatColor", "TermExtendMatch", "MinimonInfo", "MinimonAutoUseItem", "ChargedDeletableBuff",
    ];
    /// <inheritdoc/>
    public override IReadOnlyList<string?> ChecksumFiles => Files;
    /// <inheritdoc/>
    public override ushort Opcode(OutMsg msg) => msg switch
    {
        OutMsg.NormalLogout => 0x0C18,
        OutMsg.AvatarListReq => 0x0C1F,
        OutMsg.LogoutReady => 0x1071,
        _ => throw new ArgumentOutOfRangeException(nameof(msg)),
    };
    /// <inheritdoc/>
    public override int RegenMobWidth => 149;
    /// <inheritdoc/>
    public override int LoginCharacterWidth => 235;
    /// <inheritdoc/>
    public override (int Length, int DamageBytes) SwingDamage => (16, 2);
}

/// <summary>The 2026 US client's packet set.</summary>
public sealed class Wire2026Profile : WireProfile
{
    /// <summary>Shared instance.</summary>
    public static readonly Wire2026Profile Instance = new();
    /// <inheritdoc/>
    public override WireKind Kind => WireKind.Wire2026;
    /// <inheritdoc/>
    public override ILoginChain CreateLogin(byte[] xorTable) => new Login2026(xorTable);
    /// <summary>The 53 tables the 2026 client hashes into MAP_LOGIN_REQ, in its order: the 2016 list without
    /// MapLinkPoint / MapWayPoint, then six more (measured on an official 2026 capture).</summary>
    public static readonly string[] Files =
    [
        "AbState", "ActiveSkill", "CharacterTitleData", "ChargedEffect", "ClassName", "Gather", "GradeItemOption",
        "ItemDismantle", "ItemInfo", "MapInfo", "MiniHouse", "MiniHouseFurniture", "MiniHouseObjAni", "MobInfo",
        "PassiveSkill", "Riding", "SubAbState", "UpgradeInfo", "WeaponAttrib", "WeaponTitleData",
        "MiniHouseFurnitureObjEffect", "MiniHouseEndure", "DiceDividind", "ActionViewInfo",
        "AbStateView", "ActiveSkillView", "CharacterTitleStateView", "EffectViewInfo", "ItemShopView",
        "ItemViewInfo", "MapViewInfo", "MobViewInfo", "NPCViewInfo", "PassiveSkillView", "ProduceView", "CollectCardView",
        "GTIView", "ItemViewEquipTypeInfo", "SingleData", "MarketSearchInfo", "ItemMoney", "PupMain", "ChatColor",
        "TermExtendMatch", "MinimonInfo", "MinimonAutoUseItem", "ChargedDeletableBuff",
        "DeprecatedFiles", "QuestData", "QuestEndNpc", "QuestEndItem", "QuestAction", "QuestReward",
    ];
    /// <inheritdoc/>
    public override IReadOnlyList<string?> ChecksumFiles => Files;
    /// <inheritdoc/>
    public override ushort Opcode(OutMsg msg) => msg switch
    {
        OutMsg.NormalLogout => 0x0C15,
        OutMsg.AvatarListReq => 0x0C1A,
        OutMsg.LogoutReady => 0x1071,
        _ => throw new ArgumentOutOfRangeException(nameof(msg)),
    };
    /// <summary>The US build's briefinfo records are one byte longer than the German build's.</summary>
    public const int UsExtra = 1;
    /// <inheritdoc/>
    public override int RegenMobWidth => 187 + UsExtra;
    /// <inheritdoc/>
    public override int LoginCharacterWidth => 304 + UsExtra;
    /// <inheritdoc/>
    public override (int Length, int DamageBytes) SwingDamage => (25, 4);

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> Normalize(ushort opcode, ReadOnlySpan<byte> p, ref byte[]? scratch)
    {
        var o = Wire2026Shapes.ToLegacy(opcode, p, ItemClassOf, out var translated);
        if (o is null)
        {
            if (translated) Mismatches.AddOrUpdate(opcode, 1, (_, n) => n + 1);
            return p;
        }
        return o;
    }

    /// <inheritdoc/>
    public override void AfterMapLoginAck(List<(ushort Op, byte[] Body)> zone, List<(ushort Op, byte[] Body)> wm)
    {
        // the 2026 client's game frame start-up ends with these two world-manager frames, just before MAP_LOGINCOMPLETE
        wm.Add((0x0878, Array.Empty<byte>()));
        wm.Add((0x581B, Array.Empty<byte>()));
    }

    /// <inheritdoc/>
    public override void AfterLoginComplete(List<(ushort Op, byte[] Body)> zone)
    {
        // the 2026 client's start-up block after MAP_LOGINCOMPLETE: three trade-window closes (ENDOFTRADE) and the
        // reward / charged item list requests for page 0 (wire order of an official session)
        for (var i = 0; i < 3; i++) zone.Add((0x200B, Array.Empty<byte>()));
        zone.Add((0x302C, new byte[2]));
        zone.Add((0x3020, new byte[2]));
    }
}

/// <summary>Shared login helpers.</summary>
internal static class LoginIo
{
    public static void Log(this LoginRequest r, string line) => r.Log?.Invoke(line);

    public static async Task<(ushort Op, byte[] P)> Expect(FiestaSocket s, LoginRequest r, string phase, TimeSpan timeout,
        CancellationToken ct, params ushort[] ops)
    {
        var f = await s.WaitForAsync(ops, timeout, ct, (op, p) =>
        {
            r.Log($"[{phase}] << 0x{op:X4} {p.Length} B (not awaited)");
            return Task.CompletedTask;
        });
        if (f is null) throw new LoginException($"{phase}: timed out waiting for {string.Join("/", ops.Select(o => $"0x{o:X4}"))}");
        return f.Value;
    }
}

/// <summary>The 2016 login chain: version check, US_LOGIN, XTRAP + world status, world select, WM login with the OTP,
/// avatar list, optional create, char login, the zone endpoint.</summary>
public sealed class Login2016(byte[] xorTable) : ILoginChain
{
    // 2016 USER / CHAR / AVATAR opcodes
    private const ushort VersionCheckReq = 0x0C65, UsLoginReq = 0x0C5A, XtrapReq = 0x0C04, WorldStatusReq = 0x0C1B;
    private const ushort LoginAck = 0x0C0A, LoginFailAck = 0x0C09, WorldSelectReq = 0x0C0B, WorldSelectAck = 0x0C0C;
    private const ushort LoginWorldReq = 0x0C0F, LoginWorldAck = 0x0C14, LoginWorldFailAck = 0x0C15;
    private const ushort GameTimeReq = 0x080D, CharLoginReq = 0x1001, CharLoginFailAck = 0x1002, CharLoginAck = 0x1003;
    private const ushort TutorialPopupReq = 0x1110, TutorialPopupAck = 0x1111;
    private const ushort AvatarCreateReq = 0x1401, AvatarCreateDataFail = 0x1403, AvatarCreateFail = 0x1404, AvatarCreateSucc = 0x1406;
    private const int AvatarSize = 130;

    /// <summary>Client identity the 2016 login checks.</summary>
    public string VersionKey { get; init; } = "10022024000000";
    /// <summary>Build tag sent in US_LOGIN_REQ.</summary>
    public string SpawnAppsTag { get; init; } = "Original";
    /// <summary>XTrap key.</summary>
    public byte[] XtrapKey { get; init; } = Encoding.ASCII.GetBytes("33B543B0CA6E7C41E5D1D0651307\0");

    /// <inheritdoc/>
    public async Task<LoginResult> RunAsync(LoginRequest r, CancellationToken ct)
    {
        byte[] otp; Endpoint wmEp;
        await using (var login = await FiestaSocket.ConnectAsync(r.Login, xorTable, ct))
        {
            login.Tap = r.Tap;
            r.Log($"[login] connected {r.Login} seed=0x{login.Seed:X4}");
            var ver = new byte[64]; Ascii.Put(ver, VersionKey);
            await login.SendAsync(VersionCheckReq, ver, ct);
            var req = new byte[316];
            Ascii.Put(req.AsSpan(0, 260), r.Username);
            Ascii.Put(req.AsSpan(260, 36), r.PasswordMd5);
            Ascii.Put(req.AsSpan(296, 20), SpawnAppsTag);
            await login.SendAsync(UsLoginReq, req, ct);
            var x = new byte[1 + XtrapKey.Length]; x[0] = (byte)XtrapKey.Length; XtrapKey.CopyTo(x, 1);
            await login.SendAsync(XtrapReq, x, ct);
            await login.SendAsync(WorldStatusReq, Array.Empty<byte>(), ct);
            var a = await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, LoginAck, LoginFailAck);
            if (a.Op == LoginFailAck) throw new LoginException($"LOGINFAIL error={U16(a.P, 0)}");
            await login.SendAsync(WorldSelectReq, new[] { r.World }, ct);
            var ws = await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, WorldSelectAck);
            var ip = Ascii.Get(ws.P.AsSpan(1, 16));
            wmEp = new Endpoint(r.PreferLoginHost || ip.Length == 0 ? r.Login.Host : ip, U16(ws.P, 17));
            otp = ws.P.AsSpan(19, 64).ToArray();
            r.Log($"[login] world select status={ws.P[0]} wm={ip}:{wmEp.Port}");
        }
        var wm = await FiestaSocket.ConnectAsync(wmEp, xorTable, ct);
        try
        {
            wm.Tap = r.Tap;
            var lw = new byte[320];
            Ascii.Put(lw.AsSpan(0, 256), r.Username);
            otp.CopyTo(lw, 256);
            await wm.SendAsync(LoginWorldReq, lw, ct);
            var av = await LoginIo.Expect(wm, r, "wm", TimeSpan.FromSeconds(15), ct, LoginWorldAck, LoginWorldFailAck);
            if (av.Op == LoginWorldFailAck) throw new LoginException($"LOGINWORLDFAIL err={U16(av.P, 0)}");
            var wmHandle = U16(av.P, 0);
            var avatars = new List<Avatar>();
            for (var i = 0; i < av.P[2] && 3 + (i + 1) * AvatarSize <= av.P.Length; i++)
                avatars.Add(Avatar.Parse(av.P.AsSpan(3 + i * AvatarSize, AvatarSize)));
            r.Log($"[wm] avatars: {string.Join(", ", avatars.Select(v => $"{v.Name}(slot {v.Slot}, lv {v.Level}, {v.Map})"))}");
            await wm.SendAsync(GameTimeReq, Array.Empty<byte>(), ct);
            var sel = Pick(avatars, r);
            if (sel is null)
            {
                if (!r.Create || r.Character is null || r.ClassId is null) throw new LoginException($"character '{r.Character}' not on the account");
                var slot = r.Slot ?? FreeSlot(avatars);
                var body = new byte[25];
                body[0] = slot;
                Ascii.Put(body.AsSpan(1, 20), r.Character);
                body[21] = (byte)((RaceFor(r.ClassId.Value) & 3) | ((r.ClassId.Value & 31) << 2) | ((r.Male ? 1 : 0) << 7));
                await wm.SendAsync(AvatarCreateReq, body, ct);
                var c = await LoginIo.Expect(wm, r, "wm", TimeSpan.FromSeconds(10), ct, AvatarCreateSucc, AvatarCreateFail, AvatarCreateDataFail);
                if (c.Op != AvatarCreateSucc) throw new LoginException($"AVATAR_CREATE refused 0x{c.Op:X4} err={(c.P.Length >= 2 ? U16(c.P, c.P.Length - 2) : -1)}");
                sel = Avatar.Parse(c.P.AsSpan(1, AvatarSize));
                r.Log($"[wm] created {sel.Name} slot {sel.Slot} class {sel.Class}");
            }
            await wm.SendAsync(CharLoginReq, new[] { sel.Slot }, ct);
            while (true)
            {
                var f = await LoginIo.Expect(wm, r, "wm", TimeSpan.FromSeconds(15), ct, CharLoginAck, CharLoginFailAck, TutorialPopupReq);
                if (f.Op == TutorialPopupReq) { await wm.SendAsync(TutorialPopupAck, new byte[] { 1 }, ct); continue; }
                if (f.Op == CharLoginFailAck) throw new LoginException($"CHAR_LOGINFAIL 0x{U16(f.P, 0):X4}");
                var zip = Ascii.Get(f.P.AsSpan(0, 16));
                var zone = new Endpoint(r.PreferLoginHost || zip.Length == 0 ? r.Login.Host : zip, U16(f.P, 16));
                r.Log($"[wm] zone {zip}:{zone.Port}");
                return new LoginResult(true, null, zone, wmHandle, sel.Name, sel.Level, sel.Class, sel.Map) { WorldManager = wm };
            }
        }
        catch
        {
            await wm.DisposeAsync();
            throw;
        }
    }

    // The 2016 avatar create needs the race of the class line (Human for fighter/cleric, Elf for archer, DarkElf for
    // mage/joker lines; a wrong race makes the create fail). Proven on the 2016 servers; there is no client table
    // that maps a class to its creation race, so it is part of this profile.
    private static int RaceFor(int classId) => classId switch { >= 1 and <= 10 => 1, >= 11 and <= 15 => 2, >= 16 and <= 25 => 3, _ => 1 };

    internal static Avatar? Pick(List<Avatar> avatars, LoginRequest r)
        => r.Character is { Length: > 0 } n
            ? avatars.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))
            : avatars.FirstOrDefault();

    internal static byte FreeSlot(List<Avatar> avatars)
    {
        for (byte s = 0; s < 255; s++) if (avatars.All(a => a.Slot != s)) return s;
        throw new LoginException("no free avatar slot");
    }

    internal static ushort U16(byte[] p, int at) => (ushort)(p[at] | (p[at + 1] << 8));
}

/// <summary>One avatar from the character list (the shared first 48 bytes of both packet sets' records).</summary>
public sealed record Avatar(uint RegNum, string Name, int Level, byte Slot, string Map, int Class, bool Male)
{
    /// <summary>Parse {chrregnum u32, name[20], level u16, slot u8, loginmap[12], delinfo[5], shape[4], ...}.</summary>
    public static Avatar Parse(ReadOnlySpan<byte> a)
        => new(BinaryPrimitives.ReadUInt32LittleEndian(a), Ascii.Get(a.Slice(4, 20)), a[24] | (a[25] << 8), a[26],
               Ascii.Get(a.Slice(27, 12)), (a[44] >> 2) & 31, ((a[44] >> 7) & 1) == 1);
}

/// <summary>The 2026 US client's login: version key, a 349-byte login, two acks, the world list, a challenge answer
/// and the OTP, world select, then the world manager's US login (user[256] + validate[64]), a 161-byte-per-character
/// list, GAMETIME, char login.</summary>
public sealed class Login2026(byte[] xorTable) : ILoginChain
{
    private const ushort Version = 0x0C2E, VersionAck = 0x0C2F, VersionMismatch = 0x0C30, PasswordCheck = 0x0C07;
    private const ushort Login = 0x0C01, Ack1 = 0x0C42, Ack2 = 0x0C49, WorldList = 0x0C06, Challenge = 0x0C40;
    private const ushort WillSelect = 0x0C36, ChallengeAnswer = 0x0C41, Otp = 0x0C37, WorldSelect = 0x0C0A, WorldSelectAck = 0x0C0B;
    private const ushort WmLogin = 0x0C0E, WmAvatars = 0x0C0F, GameTimeReq = 0x080D;
    private const ushort CharLogin = 0x1001, CharLoginFail = 0x1002, CharLoginAck = 0x1003, TutorialReq = 0x1110, TutorialAck = 0x1111;
    private const ushort CreateReq = 0x1401, CreateFail = 0x1404, CreateSucc = 0x1406;
    private const int AvatarSize = 161, AvatarHead = 22;

    /// <summary>The 2026 US client's version key.</summary>
    public string VersionKey { get; init; } = "639f5ded4a8a2d6fdd924636511f5a85";

    /// <inheritdoc/>
    public async Task<LoginResult> RunAsync(LoginRequest r, CancellationToken ct)
    {
        byte[] validate; Endpoint wmEp;
        await using (var login = await FiestaSocket.ConnectAsync(r.Login, xorTable, ct))
        {
            login.Tap = r.Tap;
            r.Log($"[login] connected {r.Login} seed=0x{login.Seed:X4}");
            await login.SendAsync(Version, Encoding.ASCII.GetBytes(VersionKey), ct);
            var va = await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, VersionAck, VersionMismatch);
            if (va.Op == VersionMismatch) throw new LoginException("version refused (client version differs from the server)");
            // 32 zero bytes, user[260], md5 hex[36], 0, "Original", 12 zero bytes = 349 B
            var body = new byte[349];
            Ascii.Put(body.AsSpan(32, 260), r.Username);
            Ascii.Put(body.AsSpan(292, 36), r.PasswordMd5);
            Ascii.Put(body.AsSpan(329, 8), "Original");
            await login.SendAsync(Login, body, ct);
            var la = await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, Ack1, PasswordCheck);
            if (la.Op == PasswordCheck) throw new LoginException($"login refused err=0x{(la.P.Length >= 2 ? Login2016.U16(la.P, 0) : 0):X4}");
            await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, Ack2);
            var wl = await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, WorldList);
            // {count u8, 4 header bytes} then {worldno u8, name[16], status u8} per world
            for (var i = 0; i < (wl.P.Length > 0 ? wl.P[0] : 0) && 5 + 18 * i + 18 <= wl.P.Length; i++)
                r.Log($"[login] world {wl.P[5 + 18 * i]} '{Ascii.Get(wl.P.AsSpan(6 + 18 * i, 16))}' status {wl.P[5 + 18 * i + 17]}");
            await login.WaitForAsync(new[] { Challenge }, TimeSpan.FromSeconds(5), ct);
            await login.SendAsync(WillSelect, Convert.FromHexString("05803a0106000000"), ct);
            var ans = new byte[65];
            Array.Fill(ans, (byte)'0', 0, 64);
            await login.SendAsync(ChallengeAnswer, ans, ct);
            await login.WaitForAsync(new[] { Otp }, TimeSpan.FromSeconds(5), ct);
            await login.SendAsync(WorldSelect, new[] { r.World }, ct);
            var ws = await LoginIo.Expect(login, r, "login", TimeSpan.FromSeconds(10), ct, WorldSelectAck);
            var ip = Ascii.Get(ws.P.AsSpan(1, 16));
            wmEp = new Endpoint(r.PreferLoginHost || ip.Length == 0 ? r.Login.Host : ip, Login2016.U16(ws.P, 17));
            validate = ws.P.AsSpan(19, 64).ToArray();
            r.Log($"[login] world select status={ws.P[0]} wm={ip}:{wmEp.Port}");
        }
        var wm = await FiestaSocket.ConnectAsync(wmEp, xorTable, ct);
        try
        {
            wm.Tap = r.Tap;
            var lw = new byte[320];
            Ascii.Put(lw.AsSpan(0, 256), r.Username);
            validate.CopyTo(lw, 256);
            await wm.SendAsync(WmLogin, lw, ct);
            var av = await LoginIo.Expect(wm, r, "wm", TimeSpan.FromSeconds(15), ct, WmAvatars);
            var wmHandle = Login2016.U16(av.P, 0);
            var avatars = new List<Avatar>();
            for (var i = 0; i < av.P[2] && AvatarHead + (i + 1) * AvatarSize <= av.P.Length; i++)
                avatars.Add(Avatar.Parse(av.P.AsSpan(AvatarHead + i * AvatarSize, AvatarSize)));
            r.Log($"[wm] handle {wmHandle}, avatars: {string.Join(", ", avatars.Select(v => $"{v.Name}(slot {v.Slot}, lv {v.Level}, {v.Map})"))}");
            await wm.SendAsync(GameTimeReq, Array.Empty<byte>(), ct);
            var sel = Login2016.Pick(avatars, r);
            if (sel is null)
            {
                if (!r.Create || r.Character is null || r.ClassId is null) throw new LoginException($"character '{r.Character}' not on the account");
                var body = new byte[25];
                body[0] = r.Slot ?? Login2016.FreeSlot(avatars);
                Ascii.Put(body.AsSpan(1, 20), r.Character);
                // the 2026 client keeps the race bits at 1 and sets only the class and gender bits
                body[21] = (byte)(1 | ((r.ClassId.Value & 31) << 2) | ((r.Male ? 1 : 0) << 7));
                await wm.SendAsync(CreateReq, body, ct);
                var c = await LoginIo.Expect(wm, r, "wm", TimeSpan.FromSeconds(10), ct, CreateSucc, CreateFail);
                if (c.Op == CreateFail) throw new LoginException($"AVATAR_CREATEFAIL err=0x{(c.P.Length >= 2 ? Login2016.U16(c.P, 0) : 0):X4}");
                sel = Avatar.Parse(c.P.AsSpan(1, AvatarSize));
                r.Log($"[wm] created {sel.Name} slot {sel.Slot} class {sel.Class} map {sel.Map}");
            }
            await wm.SendAsync(CharLogin, new[] { sel.Slot }, ct);
            while (true)
            {
                var f = await LoginIo.Expect(wm, r, "wm", TimeSpan.FromSeconds(15), ct, CharLoginAck, CharLoginFail, TutorialReq);
                if (f.Op == TutorialReq) { await wm.SendAsync(TutorialAck, new byte[] { 1 }, ct); r.Log("[wm] tutorial skipped"); continue; }
                if (f.Op == CharLoginFail) throw new LoginException($"CHAR_LOGINFAIL 0x{(f.P.Length >= 2 ? Login2016.U16(f.P, 0) : 0):X4}");
                var zip = Ascii.Get(f.P.AsSpan(0, 16));
                var zone = new Endpoint(r.PreferLoginHost || zip.Length == 0 ? r.Login.Host : zip, Login2016.U16(f.P, 16));
                r.Log($"[wm] zone {zip}:{zone.Port}");
                return new LoginResult(true, null, zone, wmHandle, sel.Name, sel.Level, sel.Class, sel.Map) { WorldManager = wm };
            }
        }
        catch
        {
            await wm.DisposeAsync();
            throw;
        }
    }
}
