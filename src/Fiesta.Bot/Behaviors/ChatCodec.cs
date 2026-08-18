using Fiesta.Bot.Session;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Behaviors;

/// <summary>Hand-rolled codec for in-zone chat</summary>
public static class ChatCodec
{
    /// <summary>C→S ACT_CHAT_REQ opcode (resolved from the typed struct's attribute)</summary>
    public static readonly ushort ChatReqOpcode = PacketRegistry.GetOpcode<PROTO_NC_ACT_CHAT_REQ>();

    /// <summary>S→C ACT_SOMEONECHAT_CMD opcode</summary>
    public static readonly ushort SomeoneChatOpcode = PacketRegistry.GetOpcode<PROTO_NC_ACT_SOMEONECHAT_CMD>();

    /// <summary>C→S ACT_WHISPER_REQ opcode</summary>
    public static readonly ushort WhisperReqOpcode = PacketRegistry.GetOpcode<PROTO_NC_ACT_WHISPER_REQ>();

    /// <summary>C→S ACT_PARTYCHAT_REQ opcode</summary>
    public static readonly ushort PartyChatReqOpcode = PacketRegistry.GetOpcode<PROTO_NC_ACT_PARTYCHAT_REQ>();

    /// <summary>Build a WHISPER_REQ: [itemLinkDataCount=0][receiver Name5(20)][len][text]</summary>
    public static FiestaPacket BuildWhisperReq(string receiver, string text)
    {
        var name = FiestaText.Encode(receiver);
        var body = FiestaText.Encode(text);
        if (body.Length > 255) body = body[..255];
        var payload = new byte[1 + 20 + 1 + body.Length];
        payload[0] = 0;                               // itemLinkDataCount
        Array.Copy(name, 0, payload, 1, Math.Min(name.Length, 20)); // receiver Name5, NUL-padded
        payload[21] = (byte)body.Length;              // len
        body.CopyTo(payload.AsSpan(22));
        return new FiestaPacket(WhisperReqOpcode, payload);
    }

    /// <summary>Build a CHAT_REQ frame for plain (no item-link) text</summary>
    public static FiestaPacket BuildChatReq(string text)
    {
        var body = FiestaText.Encode(text);
        if (body.Length > 255) body = body[..255]; // len is a single byte
        var payload = new byte[2 + body.Length];
        payload[0] = 0;                  // itemLinkDataCount
        payload[1] = (byte)body.Length;  // len
        body.CopyTo(payload.AsSpan(2));
        return new FiestaPacket(ChatReqOpcode, payload);
    }

    /// <summary>Build a PARTYCHAT_REQ frame: identical layout to CHAT_REQ ([itemLinkDataCount=0][len][text]) on the party-chat…</summary>
    public static FiestaPacket BuildPartyChatReq(string text)
    {
        var body = FiestaText.Encode(text);
        if (body.Length > 255) body = body[..255];
        var payload = new byte[2 + body.Length];
        payload[0] = 0;                  // itemLinkDataCount
        payload[1] = (byte)body.Length;  // len
        body.CopyTo(payload.AsSpan(2));
        return new FiestaPacket(PartyChatReqOpcode, payload);
    }

    /// <summary>Parse a SOMEONECHAT payload into the speaker's zone handle and text</summary>
    public static bool TryDecodeSomeoneChat(ReadOnlySpan<byte> payload, out ushort handle, out string text)
    {
        handle = 0;
        text = string.Empty;
        if (payload.Length < 7) return false;

        handle = (ushort)(payload[1] | (payload[2] << 8));
        int len = payload[3];
        var textStart = 7;
        if (textStart + len > payload.Length)
            len = payload.Length - textStart; // be lenient if len overruns the frame
        if (len <= 0) return true;            // empty message, valid handle

        text = FiestaText.Decode(payload.Slice(textStart, len));
        return true;
    }
}
