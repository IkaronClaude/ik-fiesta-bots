using System.Security.Cryptography;

namespace Fiesta.Bot.Zone;

/// <summary>The zone's per-login anti-cheat: [1801] carries 49 data-file checksums that the zone compares against its own…</summary>
public static class DataFileChecksums
{
    /// <summary>The 49 files, in the exact order the zone checks them (idx 0..48)</summary>
    public static readonly string[] Files =
    [
        "AbState", "ActiveSkill", "CharacterTitleData", "ChargedEffect", "ClassName",
        "Gather", "GradeItemOption", "ItemDismantle", "ItemInfo", "MapInfo",
        "MiniHouse", "MiniHouseFurniture", "MiniHouseObjAni", "MobInfo", "PassiveSkill",
        "Riding", "SubAbState", "UpgradeInfo", "WeaponAttrib", "WeaponTitleData",
        "MiniHouseFurnitureObjEffect", "MiniHouseEndure", "DiceDividind", "ActionViewInfo", "MapLinkPoint",
        "MapWayPoint", "AbStateView", "ActiveSkillView", "CharacterTitleStateView", "EffectViewInfo",
        "ItemShopView", "ItemViewInfo", "MapViewInfo", "MobViewInfo", "NPCViewInfo",
        "PassiveSkillView", "ProduceView", "CollectCardView", "GTIView", "ItemViewEquipTypeInfo",
        "SingleData", "MarketSearchInfo", "ItemMoney", "PupMain", "ChatColor",
        "TermExtendMatch", "MinimonInfo", "MinimonAutoUseItem", "ChargedDeletableBuff",
    ];

    /// <summary>checksum = MD5(file[:0x24] + Encryption(file[0x24:])) as lowercase hex</summary>
    public static string Compute(string shnPath)
    {
        var d = File.ReadAllBytes(shnPath);
        if (d.Length < 0x24)
            throw new InvalidDataException($"{shnPath} is too short ({d.Length} bytes) for a .shn header");
        var enc = Encryption.Apply(d.AsSpan(0x24));
        var buf = new byte[0x24 + enc.Length];
        Array.Copy(d, 0, buf, 0, 0x24);
        Array.Copy(enc, 0, buf, 0x24, enc.Length);
        return Convert.ToHexString(MD5.HashData(buf)).ToLowerInvariant();
    }

    /// <summary>
    /// Tables a 2026 client does not check: it sends no checksum for them, and the zone (Fiesta2026on2016's zone plugin
    /// client_tables, "unchecked" in ClientTableLayouts.txt) expects 32 '0's in their slots - what the proxy forwarded for
    /// a real 2026 client. Used when the bot plays as a 2026-shape client (FIESTA_WIRE=2026, Net.Wire2026).
    /// </summary>
    public static readonly string[] UncheckedBy2026Client = ["MapLinkPoint", "MapWayPoint"];

    /// <summary>Compute all 49 checksums from a client ressystem directory</summary>
    public static string[] ComputeAll(string ressystemDir)
    {
        var result = new string[Files.Length];
        for (var i = 0; i < Files.Length; i++)
        {
            if (Fiesta.Bot.Net.Wire2026.Enabled && Array.IndexOf(UncheckedBy2026Client, Files[i]) >= 0)
            {
                result[i] = new string('0', 32);
                continue;
            }
            var path = Path.Combine(ressystemDir, Files[i] + ".shn");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Data file #{i} ({Files[i]}.shn) not found in {ressystemDir}", path);
            result[i] = Compute(path);
        }
        return result;
    }
}
