using System.Security.Cryptography;

namespace Fiesta.Bot.Zone;

/// <summary>
/// The zone's per-login anti-cheat: [1801] carries 49 data-file checksums that
/// the zone compares against its own reference set (first mismatch → [1804]
/// with nWrongDataFileIndex). Each checksum is the 32-char lowercase hex of
/// MD5(file[:0x24] + Encryption(file[0x24:])).
///
/// The ordered file list (SHN_DATA_FILE_INDEX) was recovered by computing this
/// checksum over every .shn in a client tree and matching against a reference
/// [1801] (48/49 matched; idx 8 = ItemInfo differed because the reference
/// capture shipped a stale ItemInfo.shn). The files are BYO: the operator points
/// us at their client's <c>ressystem</c> dir, which must match the server's data.
/// </summary>
public static class DataFileChecksums
{
    /// <summary>The 49 files, in the exact order the zone checks them (idx 0..48).</summary>
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

    /// <summary>checksum = MD5(file[:0x24] + Encryption(file[0x24:])) as lowercase hex.</summary>
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

    /// <summary>Compute all 49 checksums from a client <c>ressystem</c> directory.</summary>
    public static string[] ComputeAll(string ressystemDir)
    {
        var result = new string[Files.Length];
        for (var i = 0; i < Files.Length; i++)
        {
            var path = Path.Combine(ressystemDir, Files[i] + ".shn");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Data file #{i} ({Files[i]}.shn) not found in {ressystemDir}", path);
            result[i] = Compute(path);
        }
        return result;
    }
}
