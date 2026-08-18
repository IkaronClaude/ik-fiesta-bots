using System.Text;

namespace Fiesta.Bot.Navigation;

/// <summary>A door / room-connector block from a map's .sbi (Shine Block Info), with its CENTRE already converted to WORLD…</summary>
public sealed record InstanceDoor(string Name, uint WorldX, uint WorldY);

/// <summary>Parses a .sbi door array (format from gherblino's MapDoorArray): u32 count , then per door a fixed 56-byte HEA…</summary>
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
