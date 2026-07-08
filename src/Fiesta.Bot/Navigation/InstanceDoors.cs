using System.Text;

namespace Fiesta.Bot.Navigation;

/// <summary>A door / room-connector block from a map's <c>.sbi</c> (Shine Block Info), with its CENTRE
/// already converted to WORLD coords. Instances (e.g. the JCQ promotion Job1_Dn01) lay their rooms out
/// along these doors, so the instance-clear driver walks door-to-door to traverse the map when no mob is
/// in view. Pure BYO client/server nav data — no hardcoding.</summary>
public sealed record InstanceDoor(string Name, uint WorldX, uint WorldY);

/// <summary>Parses a <c>.sbi</c> door array (format from gherblino's MapDoorArray): <c>u32 count</c>, then
/// per door a fixed 56-byte HEAD — <c>name[32]</c> (NUL-terminated) + <c>u32 startX,startY,endX,endY,
/// dataSize,address</c> (all in TILE coords) — then a mask buffer we ignore. Tile→world = ×6.25 (matches
/// <see cref="Pathfinding.BlockGrid"/>'s WorldToTile = /6.25).</summary>
public static class InstanceDoors
{
    private const int HeadSize = 32 + 6 * 4;   // name[32] + 6 u32
    private const double TileToWorld = 6.25;

    public static IReadOnlyList<InstanceDoor> Load(string sbiPath)
    {
        var doors = new List<InstanceDoor>();
        if (!File.Exists(sbiPath)) return doors;
        byte[] b;
        try { b = File.ReadAllBytes(sbiPath); } catch { return doors; }
        if (b.Length < 4) return doors;
        uint U32(int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        uint count = U32(0);
        int off = 4;
        for (int i = 0; i < count && off + HeadSize <= b.Length; i++)
        {
            int z = Array.IndexOf(b, (byte)0, off, 32);
            string name = Encoding.ASCII.GetString(b, off, z < 0 ? 32 : z - off);
            uint sx = U32(off + 32), sy = U32(off + 36), ex = U32(off + 40), ey = U32(off + 44);
            uint cx = (uint)((sx + ex) / 2.0 * TileToWorld);
            uint cy = (uint)((sy + ey) / 2.0 * TileToWorld);
            doors.Add(new InstanceDoor(name, cx, cy));
            off += HeadSize;
        }
        return doors;
    }
}
