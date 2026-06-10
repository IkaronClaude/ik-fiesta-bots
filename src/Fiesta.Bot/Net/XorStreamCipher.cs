using FiestaLibReloaded.Networking;

namespace Fiesta.Bot.Net;

/// <summary>
/// Fiesta's client→server stream cipher: each body byte is XOR'd against a
/// fixed BYO table, the position advancing per byte and wrapping mod table
/// length. The position starts at the handshake <c>seed</c> the server sends.
///
/// XOR is symmetric, but the real protocol only encrypts C→S — S→C stays
/// plaintext — so a client uses one of these for the *send* direction only.
/// The position is mutable state: every byte transformed advances it, so a
/// single instance must serialize sends (the connection holds a send lock).
///
/// The table itself is operator-provided (BYO) and never shipped in this repo;
/// see <see cref="XorTableLoader"/> and PROJECT_PLAN.md.
/// </summary>
public sealed class XorStreamCipher : IFiestaStreamCipher
{
    private readonly byte[] _table;
    private int _pos;

    public XorStreamCipher(byte[] table, int seed = 0)
    {
        if (table is null || table.Length == 0)
            throw new ArgumentException("XOR table must be non-empty", nameof(table));
        _table = table;
        _pos = ((seed % table.Length) + table.Length) % table.Length;
    }

    /// <summary>Current table position (advances as bytes are transformed).</summary>
    public int Position => _pos;

    public void Transform(Span<byte> data)
    {
        var tbl = _table;
        var n = tbl.Length;
        var pos = _pos;
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= tbl[pos];
            if (++pos >= n) pos -= n;
        }
        _pos = pos;
    }
}
