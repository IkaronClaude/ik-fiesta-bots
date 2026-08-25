
#r "C:/Projects/ik-fiesta-bots/src/Fiesta.Bot/bin/Release/net10.0/Fiesta.Bot.dll"
using System;
using System.Text.Json;
using Fiesta.Bot.Combat;

static readonly string[] SectPlus = {"PureCharParam","Item.plus","ItemPowerRate.plus","Upgrade.plus",
    "WeaponTitle.plus","PassiveSkill.plus","AbnormalState.plus","LastTune.plus"};

static ParamBlock Pick(ParamContainer c, string s) => s switch {
    "PureCharParam" => c.PureCharParam,
    "Item.plus" => c.ItemPlus, "Item.rate" => c.ItemRate,
    "ItemPowerRate.plus" => c.ItemPowerRatePlus, "ItemPowerRate.rate" => c.ItemPowerRateRate,
    "Upgrade.plus" => c.UpgradePlus, "Upgrade.rate" => c.UpgradeRate,
    "WeaponTitle.plus" => c.WeaponTitlePlus, "WeaponTitle.rate" => c.WeaponTitleRate,
    "PassiveSkill.plus" => c.PassiveSkillPlus, "PassiveSkill.rate" => c.PassiveSkillRate,
    "AbnormalState.plus" => c.AbnormalStatePlus, "AbnormalState.rate" => c.AbnormalStateRate,
    "LastTune.plus" => c.LastTunePlus, "LastTune.rate" => c.LastTuneRate,
    "Total" => c.Total, _ => throw new Exception("section " + s)
};

static ParamContainer Build(JsonElement e) {
    var c = ParamContainer.Neutral();
    if (e.ValueKind != JsonValueKind.Object) return c;
    foreach (var sect in e.EnumerateObject()) {
        var b = Pick(c, sect.Name);
        foreach (var f in sect.Value.EnumerateObject())
            b[Enum.Parse<ParamField>(f.Name)] = f.Value.GetInt32();
    }
    return c;
}

string line;
while ((line = Console.ReadLine()) != null) {
    if (line.Length == 0) continue;
    try {
        var c = JsonDocument.Parse(line).RootElement;
        var att = Build(c.GetProperty("att"));
        var def = Build(c.GetProperty("def"));
        int lvl = c.TryGetProperty("level", out var l) ? l.GetInt32() : 61;
        string fn = c.GetProperty("fn").GetString();
        double v;
        switch (fn) {
            case "roe_MinWC": v = DamageFormula.MinWc(att); break;
            case "roe_MaxWC": v = DamageFormula.MaxWc(att); break;
            case "roe_AC":    v = DamageFormula.Ac(def);    break;
            case "roe_TH":    v = DamageFormula.Th(att);    break;
            case "roe_TB":    v = DamageFormula.Tb(def);    break;
            case "roe_MR":    v = DamageFormula.Mr(def);    break;
            case "AttackPower": v = DamageFormula.AttackPower(att, c.GetProperty("roll").GetInt32()); break;
            case "DefendPower": v = DamageFormula.DefendPower(def); break;
            case "CalcDamage":
                v = DamageFormula.CalcDamage(att, def, lvl,
                        c.GetProperty("roll").GetInt32(), c.GetProperty("crit").GetBoolean(),
                        c.TryGetProperty("damagerate", out var dr) ? dr.GetInt32() : 1000);
                break;
            default: throw new Exception("fn " + fn);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, v }));
    } catch (Exception ex) {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, err = ex.Message }));
    }
    Console.Out.Flush();
}
