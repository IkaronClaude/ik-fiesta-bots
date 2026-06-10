using System.Text;

namespace Fiesta.Bot.Session;

/// <summary>
/// Encoding helpers for Fiesta wire strings. The Korean client/server encode text
/// in code page 949 (EUC-KR); ASCII is a subset, so English names/chat round-trip
/// unchanged. Fixed-width name fields (Name5 = 20 bytes) are NUL-padded — we trim
/// at the first NUL on decode.
/// </summary>
public static class FiestaText
{
    private static readonly Encoding Korean = ResolveKorean();

    private static Encoding ResolveKorean()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949);
        }
        catch
        {
            return Encoding.Latin1; // byte-preserving fallback if 949 is unavailable
        }
    }

    /// <summary>Decode a NUL-terminated (or full-width) EUC-KR field to a string.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return bytes.IsEmpty ? string.Empty : Korean.GetString(bytes);
    }

    /// <summary>Encode a string to EUC-KR bytes (no NUL terminator added).</summary>
    public static byte[] Encode(string text) => Korean.GetBytes(text ?? string.Empty);
}
