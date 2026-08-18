namespace Fiesta.Bot.Login;

/// <summary>A server endpoint (host + port)</summary>
public readonly record struct FiestaEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>Credentials for a bot account</summary>
public readonly record struct BotCredentials(string Username, string PasswordMd5)
{
    public static BotCredentials FromPlaintext(string username, string plaintextPassword)
        => new(username, Md5Hex(plaintextPassword));

    private static string Md5Hex(string s)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
               System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>Result of the Login phase: the one-time pad + the WorldManager endpoint</summary>
public sealed record LoginPhaseResult(
    byte[] Otp,                 // validate_new, 64 bytes, echoed into LOGINWORLD_REQ
    FiestaEndpoint WmAdvertised, // ip/port as WORLDSELECT_ACK advertised them
    byte WorldStatus);

/// <summary>One avatar (character) on the account, as LOGINWORLD_ACK reports it</summary>
public sealed record AvatarSummary(uint ChrRegNum, string Name, byte Slot, ushort Level, string LoginMap = "", byte Class = 0);

/// <summary>Result of the WM phase: the zone endpoint to enter + the live WM handle</summary>
public sealed record WmPhaseResult(
    ushort WmHandle,
    IReadOnlyList<AvatarSummary> Avatars,
    AvatarSummary? Selected,
    FiestaEndpoint? ZoneAdvertised);

/// <summary>Client-build identity values the Login server checks during the handshake</summary>
public sealed record ClientProfile(string VersionKey, string SpawnAppsTag, byte[] XtrapKey)
{
    public static readonly ClientProfile ClientProd2 = new(
        VersionKey: "10022024000000",
        SpawnAppsTag: "Original",
        XtrapKey: System.Text.Encoding.ASCII.GetBytes("33B543B0CA6E7C41E5D1D0651307\0"));
}

public sealed class LoginChainException : Exception
{
    public LoginChainException(string message) : base(message) { }
}
