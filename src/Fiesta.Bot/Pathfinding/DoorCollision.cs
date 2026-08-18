namespace Fiesta.Bot.Pathfinding;

/// <summary>A map's DYNAMIC door collision, loaded from its .sbi (gherblino's MapDoorArray)</summary>
public sealed class DoorCollision
{
    /// <summary>One door's box (inclusive TILE bounds) and its two per-state walkability bitmaps</summary>
    public sealed class Door
    {
        public required string Name { get; init; }
        public required int StartX { get; init; }
        public required int StartY { get; init; }
        public required int EndX { get; init; }
        public required int EndY { get; init; }
        public required int WBytes { get; init; }        // bytes per bitmap row = width/8
        public required byte[][] State { get; init; }    // State[0] = closed, State[1] = open (== base .shbd)

        public int Width => EndX - StartX + 1;
        public int Height => EndY - StartY + 1;

        /// <summary>Is local tile (lx,ly) blocked in the given door (0 closed / 1 open)?</summary>
        public bool BlockedLocal(int state, int lx, int ly)
        {
            if ((uint)state >= (uint)State.Length) return false;
            if ((uint)lx >= (uint)Width || (uint)ly >= (uint)Height) return false;
            var buf = State[state];
            int bit = ly * WBytes + (lx >> 3);
            return bit < buf.Length && ((buf[bit] >> (lx & 7)) & 1) != 0;
        }
    }

    public IReadOnlyList<Door> Doors { get; }
    private DoorCollision(IReadOnlyList<Door> doors) => Doors = doors;

    /// <summary>Parse a .sbi . Returns null if the file is absent, empty, or malformed (caller then runs with the static grid…</summary>
    public static DoorCollision? Load(string sbiPath)
    {
        if (!File.Exists(sbiPath)) return null;
        byte[] b;
        try { b = File.ReadAllBytes(sbiPath); } catch { return null; }
        if (b.Length < 4) return null;
        int U32(int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        int count = U32(0);
        if (count <= 0 || count >= 32) return null; // MapDoorArray caps at <32 doors; guard garbage
        const int headSize = 32 + 6 * 4;
        int headEnd = 4 + count * headSize;
        if (headEnd > b.Length) return null;

        var heads = new (string name, int sx, int sy, int ex, int ey, int ds, int addr)[count];
        for (int i = 0; i < count; i++)
        {
            int off = 4 + i * headSize;
            int z = Array.IndexOf(b, (byte)0, off, 32);
            string name = System.Text.Encoding.ASCII.GetString(b, off, z < 0 ? 32 : z - off);
            heads[i] = (name, U32(off + 32), U32(off + 36), U32(off + 40), U32(off + 44), U32(off + 48), U32(off + 52));
        }

        var doors = new List<Door>(count);
        foreach (var (name, sx, sy, ex, ey, ds, addr) in heads)
        {
            if (ex < sx || ey < sy || ds <= 0) continue;
            int wBytes = (ex - sx + 1) / 8;
            if (wBytes <= 0) continue;
            int start = headEnd + addr;
            if (start + 2 * ds > b.Length) continue; // truncated → skip this door (grid still works, just no overlay)
            var closed = new byte[ds];
            var open = new byte[ds];
            Array.Copy(b, start, closed, 0, ds);
            Array.Copy(b, start + ds, open, 0, ds);
            doors.Add(new Door
            {
                Name = name, StartX = sx, StartY = sy, EndX = ex, EndY = ey,
                WBytes = wBytes, State = new[] { closed, open },
            });
        }
        return doors.Count > 0 ? new DoorCollision(doors) : null;
    }
}
