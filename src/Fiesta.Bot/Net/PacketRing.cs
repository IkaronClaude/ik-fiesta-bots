namespace Fiesta.Bot.Net;

/// <summary>One captured frame: when, which way it went, its opcode and its raw bytes.</summary>
public sealed record RingFrame(DateTime AtUtc, bool Outbound, ushort Opcode, byte[] Payload);

/// <summary>
/// A small always-on ring of the most recent frames in BOTH directions, so a post-mortem (a death, a
/// stall, a refused action) can show what was actually on the wire in the moments before it — without
/// anyone having had the foresight to enable the file packet log first.
///
/// This is deliberately separate from <see cref="PacketLog"/>: that writes every frame to disk and is
/// opt-in per bot, whereas this keeps a bounded window in memory and is always running. The two coexist
/// on the same tap.
///
/// Bounded by construction: a fixed-size array reused in place, so a long-running bot cannot grow it.
/// Payloads are copied because the tap hands out pooled/reused memory that is invalid after it returns.
/// </summary>
public sealed class PacketRing
{
    private readonly RingFrame?[] _buf;
    private readonly object _lock = new();
    private int _next;        // next slot to write
    private long _total;      // frames ever seen (so callers can tell a full ring from a partial one)

    public PacketRing(int capacity = 100) => _buf = new RingFrame?[Math.Max(1, capacity)];

    public int Capacity => _buf.Length;
    public long TotalSeen { get { lock (_lock) return _total; } }

    /// <summary>Tap signature matches <c>ISession.PacketTap</c> so it can be chained with the file log.</summary>
    public void Tap(bool outbound, ushort opcode, ReadOnlyMemory<byte> payload)
    {
        // Copy: the caller's buffer is reused once this returns, so keeping the Memory would alias whatever
        // frame lands next — the classic way a capture ends up showing the wrong bytes.
        var bytes = payload.ToArray();
        var frame = new RingFrame(DateTime.UtcNow, outbound, opcode, bytes);
        lock (_lock)
        {
            _buf[_next] = frame;
            _next = (_next + 1) % _buf.Length;
            _total++;
        }
    }

    /// <summary>The retained frames, OLDEST first. Returns at most <paramref name="max"/> (newest kept).</summary>
    public IReadOnlyList<RingFrame> Snapshot(int max = int.MaxValue)
    {
        lock (_lock)
        {
            var have = (int)Math.Min(_total, _buf.Length);
            var take = Math.Min(have, Math.Max(0, max));
            var start = _next - have;                       // index of the oldest retained frame
            var outp = new List<RingFrame>(take);
            for (var i = have - take; i < have; i++)         // skip the oldest when trimming to `take`
            {
                var idx = ((start + i) % _buf.Length + _buf.Length) % _buf.Length;
                if (_buf[idx] is { } f) outp.Add(f);
            }
            return outp;
        }
    }
}
