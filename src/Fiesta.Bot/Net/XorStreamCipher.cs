using FiestaLibReloaded.Networking;

namespace Fiesta.Bot.Net;

/// <summary>Fiesta's client→server stream cipher: each body byte is XOR'd against a fixed BYO table, the position advancin…</summary>
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

    /// <summary>Current table position (advances as bytes are transformed)</summary>
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
