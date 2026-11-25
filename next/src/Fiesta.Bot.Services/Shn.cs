using System.Text;

namespace Fiesta.Bot.Services;

/// <summary>A client data table (.shn) read into typed columns: numeric cells as long, text cells as strings. Reads
/// every column by its declared width, so a table with a column type this reader does not name still loads.</summary>
public sealed class ShnTable
{
    private static readonly Encoding Text;
    static ShnTable()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Text = Encoding.GetEncoding(949);
    }

    /// <summary>Table name.</summary>
    public string Name { get; }
    /// <summary>Column names.</summary>
    public string[] Columns { get; }
    /// <summary>Type codes per column.</summary>
    public uint[] Types { get; }
    /// <summary>Rows.</summary>
    public int RowCount { get; }
    private readonly long[][] _num;
    private readonly string?[][] _str;
    private readonly Dictionary<string, int> _col;

    private ShnTable(string name, string[] cols, uint[] types, long[][] num, string?[][] str, int rows)
    {
        Name = name; Columns = cols; Types = types; _num = num; _str = str; RowCount = rows;
        _col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cols.Length; i++) _col.TryAdd(cols[i], i);
    }

    /// <summary>Column index, -1 when absent.</summary>
    public int Col(string name) => _col.TryGetValue(name, out var i) ? i : -1;
    /// <summary>Numeric cell (0 when the column is absent or text).</summary>
    public long Num(int row, int col) => col >= 0 && _num[col] is { } c ? c[row] : 0;
    /// <summary>Numeric cell by column name.</summary>
    public long Num(int row, string col) => Num(row, Col(col));
    /// <summary>Text cell (null when absent or numeric).</summary>
    public string? Str(int row, int col) => col >= 0 && _str[col] is { } c ? c[row] : null;
    /// <summary>Text cell by column name.</summary>
    public string? Str(int row, string col) => Str(row, Col(col));

    /// <summary>Load and decode a file.</summary>
    public static ShnTable Load(string path)
    {
        var all = File.ReadAllBytes(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (all.Length < 36) throw new InvalidDataException($"{name}: too short");
        var len = BitConverter.ToInt32(all, 32) - 36;
        if (len < 0 || 36 + len > all.Length) throw new InvalidDataException($"{name}: bad length");
        var body = all.AsSpan(36, len).ToArray();
        Fiesta.Bot.Protocol.WireProfile.DataCipher(body);
        var at = 4;
        int I32() { var v = BitConverter.ToInt32(body, at); at += 4; return v; }
        var rows = I32(); var recLen = I32(); var ncol = I32();
        var cols = new string[ncol]; var types = new uint[ncol]; var lens = new int[ncol];
        for (var i = 0; i < ncol; i++)
        {
            var n = body.AsSpan(at, 48); at += 48;
            var z = n.IndexOf((byte)0);
            var cn = Encoding.ASCII.GetString(z < 0 ? n : n[..z]).Trim();
            cols[i] = cn.Length < 2 ? $"Undefined{i}" : cn;
            types[i] = (uint)I32(); lens[i] = I32();
        }
        var num = new long[ncol][]; var str = new string?[ncol][];
        for (var c = 0; c < ncol; c++)
            if (IsText(types[c])) str[c] = new string?[rows]; else num[c] = new long[rows];
        for (var r = 0; r < rows; r++)
        {
            var rowLen = BitConverter.ToUInt16(body, at);
            var rowStart = at; at += 2;
            for (var c = 0; c < ncol; c++)
            {
                var t = types[c]; var l = lens[c];
                if (t == 26)
                {
                    var z = Array.IndexOf(body, (byte)0, at);
                    if (z < 0) z = body.Length;
                    str[c]![r] = Text.GetString(body, at, z - at);
                    at = z + 1;
                    continue;
                }
                if (IsText(t))
                {
                    var s = body.AsSpan(at, l); var z = s.IndexOf((byte)0);
                    str[c]![r] = Text.GetString(z < 0 ? s : s[..z]);
                    at += l; continue;
                }
                long v = l switch
                {
                    1 => t is 20 ? (sbyte)body[at] : body[at],
                    2 => t is 13 or 21 ? BitConverter.ToInt16(body, at) : BitConverter.ToUInt16(body, at),
                    4 => t is 22 ? BitConverter.ToInt32(body, at) : t is 5 ? (long)BitConverter.ToSingle(body, at) : BitConverter.ToUInt32(body, at),
                    8 => BitConverter.ToInt64(body, at),
                    _ => 0,
                };
                num[c]![r] = v;
                at += l;
            }
            if (rowLen > 0 && at - rowStart != rowLen && !HasVarText(types)) at = rowStart + rowLen;
        }
        return new ShnTable(name, cols, types, num, str, rows);
    }

    private static bool IsText(uint t) => t is 9 or 10 or 24 or 26;
    private static bool HasVarText(uint[] t) { foreach (var x in t) if (x == 26) return true; return false; }
}
