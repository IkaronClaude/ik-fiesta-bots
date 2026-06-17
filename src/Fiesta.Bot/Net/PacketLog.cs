using System.Text;
using FiestaLibReloaded.Networking;

namespace Fiesta.Bot.Net;

/// <summary>
/// A runtime-toggleable packet tap that writes both directions of a bot's traffic to a
/// tailable text file, interleaved in arrival order, as <b>plaintext</b> (XOR-decoded)
/// opcode + canonical name + a classic hex/ASCII dump. Wire it to one or more
/// <see cref="FiestaClientConnection.PacketTap"/> / <see cref="Session.BotSession.PacketTap"/>.
///
/// This exists so we read what the server actually sent instead of guessing protocol rules —
/// e.g. exactly which packet (and bytes) a revive / HP change carries.
/// </summary>
public sealed class PacketLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public string Path { get; }

    public PacketLog(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Append so re-enabling keeps history; AutoFlush so `tail -f` sees it live.
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        _writer.WriteLine($"==== packet log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====");
    }

    /// <summary>The delegate to assign to a connection/session PacketTap.</summary>
    public void Tap(bool outbound, ushort opcode, ReadOnlyMemory<byte> payload)
    {
        // Never let logging take down the read/send path.
        try { Write(outbound, opcode, payload.Span); }
        catch { /* swallow — observability must not break traffic */ }
    }

    private void Write(bool outbound, ushort opcode, ReadOnlySpan<byte> payload)
    {
        // Opcode = (dept << 10) | cmd. Resolve the canonical struct name when known.
        int dept = opcode >> 10;
        int cmd = opcode & 0x3FF;
        string name = PacketRegistry.GetType(opcode)?.Name ?? "?";
        string arrow = outbound ? "C->S" : "S->C";

        var sb = new StringBuilder();
        sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] {arrow} 0x{opcode:X4} d={dept} c={cmd} len={payload.Length} {name}");
        sb.Append('\n');
        AppendHexDump(sb, payload);

        lock (_gate)
        {
            if (_disposed) return;
            _writer.Write(sb.ToString());
        }
    }

    // Classic 16-byte hex/ASCII rows: "  0000  AA BB CC ...   |ascii|"
    private static void AppendHexDump(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        for (int off = 0; off < data.Length; off += 16)
        {
            int n = Math.Min(16, data.Length - off);
            sb.Append("  ").Append(off.ToString("X4")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (i < n) sb.Append(data[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' '); // gap between the two 8-byte halves
            }
            sb.Append(" |");
            for (int i = 0; i < n; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append("|\n");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.WriteLine($"==== packet log closed {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====");
                _writer.Dispose();
            }
            catch { /* ignore */ }
        }
    }
}
