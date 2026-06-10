namespace Fiesta.Bot.Zone;

/// <summary>
/// The Fiesta data-file obfuscation used as a building block of the [1801]
/// anti-cheat checksum (see <see cref="DataFileChecksums"/>): the zone hashes
/// each reference .shn as MD5(file[:0x24] + Encrypt(file[0x24:])).
///
/// The algorithm is lifted from the Zone.exe routine whose PDB symbol is
/// (mis)spelled <c>CDataReader::Encription</c> (@0x62A0B0); we spell it correctly
/// here. We only ever apply it forward.
/// </summary>
public static class Encryption
{
    public static byte[] Apply(ReadOnlySpan<byte> data)
    {
        var b = data.ToArray();
        var n = b.Length;
        var key = n & 0xFF;
        for (var i = n - 1; i >= 0; i--)
        {
            b[i] ^= (byte)key;
            var a = (i * 11) & 0xFF;
            a ^= ((i & 0xF) + 0x55) & 0xFF;
            a ^= key;
            a ^= 0xAA;
            key = a & 0xFF;
        }
        return b;
    }
}
