using Fiesta.Bot.Agent;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;

namespace Fiesta.Bot.Api;

/// <summary>Lua module `produce`: production recipes from the client's Produce table (the recipe id is the production
/// skill id, learned like any skill) and the production cast.</summary>
[LuaModule("produce")]
public sealed partial class ProduceModule(AgentContext ctx) : ApiModule(ctx)
{
    private ProduceRow? Row(int id) => (Ctx.Data as ClientData)?.Produce(id);
    private readonly Intent[] _one = new Intent[1];

    /// <summary>Recipes we have learned (production skills with a Produce row).</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter Recipes()
    {
        var s = Ctx.Scratch(256); var n = 0;
        foreach (var id in Ctx.World.Skills.Learned) if (n < s.Length && Row(id) is not null) s[n++] = id;
        return ApiScope.Iter(s[..n]);
    }
    /// <summary>What one cast of a recipe makes: item id, lot (-1, 0 unknown recipe).</summary>
    [LuaFn] public (int ItemId, int Lot) Product(int id) => Row(id) is { } r ? (r.Product, r.Lot) : (-1, 0);
    /// <summary>Material i (1-based) of a recipe: item id, quantity (-1, 0 none).</summary>
    [LuaFn] public (int ItemId, int Quantity) Raw(int id, int i) => Row(id) is { } r && i >= 1 && i <= r.Raws.Length ? r.Raws[i - 1] : (-1, 0);
    /// <summary>How many casts the bag's materials allow (0 when a material is short or the recipe is unknown).</summary>
    [LuaFn]
    public int Makeable(int id)
    {
        if (Row(id) is not { Raws.Length: > 0 } r) return 0;
        var n = int.MaxValue;
        foreach (var (it, q) in r.Raws) n = Math.Min(n, Ctx.World.Bag.Count(it) / q);
        return n;
    }
    /// <summary>Mastery the recipe needs (type, amount) and our mastery total of a skill (-1 not seen yet).</summary>
    [LuaFn] public (int Type, int Needed) Needs(int id) => Row(id) is { } r ? (r.NeededMasteryType, r.NeededMasteryGain) : (-1, 0);
    /// <summary>The production field a recipe belongs to (ProduceView id = the main skill to choose), -1 none.</summary>
    [LuaFn] public int FieldOf(int id) => (Ctx.Data as ClientData)?.ProduceField(id) ?? -1;
    /// <summary>Was a production field chosen (acked) this session?</summary>
    [LuaFn] public bool HasField(int mainSkill) => Ctx.World.Skills.Fields.Contains(mainSkill);
    /// <summary>Does a recipe we know use this item as a material?</summary>
    [LuaFn]
    public bool UsesRaw(int itemId)
    {
        foreach (var id in Ctx.World.Skills.Learned)
            if (Row(id) is { } r) foreach (var (it, _) in r.Raws) if (it == itemId) return true;
        return false;
    }
    /// <summary>Every recipe of a production field (the ProduceView main skill), learned or not, from client data.</summary>
    [LuaFn(Cost = Fiesta.Bot.Api.Cost.On, Alloc = Alloc.Iterator)]
    public LuaIter OfField(int mainSkill)
    {
        var s = Ctx.Scratch(1024); var n = 0;
        if (Ctx.Data is ClientData cd) foreach (var id in cd.RecipesOfField(mainSkill)) if (n < s.Length) s[n++] = id;
        return ApiScope.Iter(s[..n]);
    }
    /// <summary>Our mastery in a recipe's mastery type: the highest total the server sent for any recipe of that type
    /// (SKILLEXP_CLIENT names a recipe; GUESS: the totals of one type are shared), 0 when none was sent yet.</summary>
    [LuaFn]
    public long TypeMastery(int id)
    {
        if (Row(id) is not { } r) return 0;
        long best = 0;
        foreach (var (skill, v) in Ctx.World.Skills.Mastery) if (Row(skill) is { } o && o.MasteryType == r.MasteryType && v > best) best = v;
        return best;
    }
    /// <summary>Our mastery total of a skill as the server last sent it (-1 none yet).</summary>
    [LuaFn] public long Mastery(int skill) => Ctx.World.Skills.Mastery.TryGetValue(skill, out var v) ? v : -1;
    /// <summary>Cast one production of a recipe (PRODUCE_CAST_REQ); acked on PRODUCE_MAKE 0x0B01, refused by its code.</summary>
    [LuaFn]
    public int Make(int id)
    {
        Span<byte> b = [(byte)id, (byte)(id >> 8)];
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpProduceCast, b, -1);
        return Ctx.Actions.Issue(ActionKind.Produce, CallingLayer, id, 0, _one).Value;
    }
    /// <summary>Choose a production field by its main skill (PRODUCTFIELD_REQ); acked on 0x0B01.</summary>
    [LuaFn]
    public int Field(int mainSkill)
    {
        Span<byte> b = [(byte)mainSkill, (byte)(mainSkill >> 8)];
        _one[0] = IntentEncoder.Raw(IntentEncoder.OpProductField, b, -1);
        return Ctx.Actions.Issue(ActionKind.ProductField, CallingLayer, mainSkill, 0, _one).Value;
    }
}
