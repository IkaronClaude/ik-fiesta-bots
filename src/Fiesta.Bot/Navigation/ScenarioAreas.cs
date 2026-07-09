using System.Text;

namespace Fiesta.Bot.Navigation;

/// <summary>A named scenario trigger AREA from a map's <c>.aid</c> (Area Info Data), centre + half-extents in
/// WORLD coords. The server's scenario script arms <c>AreaEntry "Name"</c> interrupts on these polygons (e.g.
/// the JCQ promotion Job1_Dn01: <c>Zone_Mob01..05</c>), each firing ONCE when the player crosses in. The
/// instance driver walks to the CURRENT armed area's centre (matched to <see cref="ZoneView.LastScenarioArea"/>)
/// so it triggers each room's wave cleanly IN ORDER instead of blind-sweeping across areas and consuming the
/// one-shot interrupts out of sequence. Pure BYO nav data — no hardcoding.</summary>
public sealed record ScenarioArea(string Name, float CenterX, float CenterY, float HalfX, float HalfY);

/// <summary>Parses a <c>.aid</c> (format from gherblino's AIDEditor): <c>u32 count</c>, then per area a
/// <c>name[32]</c> (NUL-terminated UTF8) + <c>i32 areaType</c> (0=circle, else square), then the shape
/// floats — circle: <c>cx,cy,radius</c>; square (oriented box): <c>cx,cy,radiusU,radiusV,angle</c>. Coords are
/// already WORLD units. We keep the axis-aligned half-extents (radiusU/V, or radius for a circle) — enough to
/// walk to the centre and test containment; the rotation is ignored for that purpose.</summary>
public static class ScenarioAreas
{
    public static IReadOnlyList<ScenarioArea> Load(string aidPath)
    {
        var areas = new List<ScenarioArea>();
        if (!File.Exists(aidPath)) return areas;
        byte[] b;
        try { b = File.ReadAllBytes(aidPath); } catch { return areas; }
        if (b.Length < 4) return areas;
        int off = 0;
        uint U32() { uint v = (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)); off += 4; return v; }
        float F32() { float v = BitConverter.ToSingle(b, off); off += 4; return v; }
        uint count = U32();
        for (int i = 0; i < count && off + 36 <= b.Length; i++)
        {
            int z = Array.IndexOf(b, (byte)0, off, 32);
            string name = Encoding.UTF8.GetString(b, off, z < 0 ? 32 : z - off);
            off += 32;
            int type = (int)U32();
            float cx = F32(), cy = F32();
            float hx, hy;
            if (type == 0) { float r = F32(); hx = hy = r; }               // circle: cx,cy,radius
            else { float ru = F32(), rv = F32(); F32(); hx = ru; hy = rv; } // square: cx,cy,radiusU,radiusV,angle
            areas.Add(new ScenarioArea(name, cx, cy, hx, hy));
        }
        return areas;
    }
}
