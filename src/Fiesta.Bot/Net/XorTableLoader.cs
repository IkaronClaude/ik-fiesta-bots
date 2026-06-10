using System.Globalization;
using System.Text;

namespace Fiesta.Bot.Net;

/// <summary>
/// Loads the BYO XOR table at runtime. The table is intentionally kept out of
/// this repo (and out of git history of the sibling repos) — see PROJECT_PLAN.md.
///
/// Sources, in priority order:
///   1. <c>XOR_TABLE_HEX</c>  — inline hex string ("0759694A…" or "0x07,0x59,…";
///                              whitespace, commas and 0x prefixes tolerated).
///   2. <c>XOR_TABLE_PATH</c> — path to a file holding either a hex string (any
///                              of the above forms) or the raw binary table. Hex
///                              is tried first; non-hex content is treated as raw.
///   3. (neither set)         — returns null. The bot can't connect to a zone
///                              without it, so callers that need it should throw
///                              a clear error.
///
/// Mirrors fiesta-proxy's <c>Crypto/XorTableLoader</c> so the same operator env
/// works for both.
/// </summary>
public static class XorTableLoader
{
    public static byte[]? FromEnvironment()
    {
        var hex = Environment.GetEnvironmentVariable("XOR_TABLE_HEX");
        if (!string.IsNullOrWhiteSpace(hex))
            return ParseHex(hex)
                ?? throw new InvalidOperationException("XOR_TABLE_HEX is set but not valid hex");

        var path = Environment.GetEnvironmentVariable("XOR_TABLE_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"XOR_TABLE_PATH '{path}' does not exist");
            var bytes = File.ReadAllBytes(path);
            if (LooksLikeHexText(bytes))
            {
                var parsed = ParseHex(Encoding.ASCII.GetString(bytes));
                if (parsed is not null) return parsed;
            }
            return bytes;
        }

        return null;
    }

    /// <summary>Load the table or throw a clear, actionable error.</summary>
    public static byte[] Require()
        => FromEnvironment()
           ?? throw new InvalidOperationException(
               "No XOR table configured. Set XOR_TABLE_HEX or XOR_TABLE_PATH " +
               "(the table is BYO and not shipped with this repo).");

    private static bool LooksLikeHexText(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        var seenHex = false;
        foreach (var b in bytes)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)',' or (byte)'x' or (byte)'X')
                continue;
            var ok =
                (b >= (byte)'0' && b <= (byte)'9') ||
                (b >= (byte)'a' && b <= (byte)'f') ||
                (b >= (byte)'A' && b <= (byte)'F');
            if (!ok) return false;
            seenHex = true;
        }
        return seenHex;
    }

    private static byte[]? ParseHex(string s)
    {
        var cleaned = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') continue;
            if (c == '0' && i + 1 < s.Length && (s[i + 1] is 'x' or 'X')) { i++; continue; }
            cleaned.Append(c);
        }
        if (cleaned.Length == 0 || (cleaned.Length & 1) != 0) return null;
        var result = new byte[cleaned.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(cleaned.ToString(i * 2, 2),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                return null;
            result[i] = b;
        }
        return result;
    }
}
