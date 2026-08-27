using MoonSharp.Interpreter;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>MoonSharp's <c>Table[int]</c> indexer is unusable for game ids, and this pins why.
///
/// <para>It broke the leveller completely for a level-1 character. <c>BotApi.questPulse</c> hands Lua a
/// <c>mobs</c> map keyed by mob id, <b>0 is a real mob id</b>, and a starter quest referencing mob 0 made
/// every single tick throw <c>"invalid key to 'next'"</c> at the first <c>pairs(snap.mobs)</c>. Characters
/// past those quests never touched mob 0, so the bug looked like "new bots are broken" rather than what it
/// was.</para></summary>
public class LuaTableIdKeyTests
{
    private static readonly Script Lua = new(CoreModules.Preset_SoftSandbox);

    /// <summary>The indexer loses the value AND poisons iteration. Both halves matter: a caller who only
    /// checked that <c>pairs</c> worked would still be silently dropping mob 0's data.</summary>
    [Fact]
    public void TheIntegerIndexerCorruptsAZeroKey()
    {
        var t = new Table(Lua);
        t[0] = DynValue.NewString("mob zero");
        t[123] = DynValue.NewString("mob 123");
        Lua.Globals["broken"] = DynValue.NewTable(t);

        Lua.DoString("return broken[0]").IsNil().ShouldBeTrue("the value written at key 0 is not readable");
        Should.Throw<ScriptRuntimeException>(
            () => Lua.DoString("for k, v in pairs(broken) do end"),
            "and the whole table becomes un-iterable, not just that entry");
    }

    /// <summary>`Table.Set(DynValue.NewNumber(id), …)` — what <c>BotApi.SetById</c> uses — is correct for
    /// every id, including the ones the indexer mangles.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(123)]
    public void SettingByNumberRoundTripsAndStaysIterable(int id)
    {
        var t = new Table(Lua);
        t.Set(DynValue.NewNumber(id), DynValue.NewString("here"));
        t.Set(DynValue.NewNumber(999), DynValue.NewString("other"));
        Lua.Globals["ok"] = DynValue.NewTable(t);

        Lua.DoString($"return ok[{id}]").String.ShouldBe("here");
        Lua.DoString("local n = 0 for k, v in pairs(ok) do n = n + 1 end return n")
            .Number.ShouldBe(2);
    }

    /// <summary>⚠️ The same table written in LUA SOURCE is fine. So this cannot be reproduced by reading the
    /// script — only by writing the table the way the host does.</summary>
    [Fact]
    public void ATableBuiltInLuaSourceHandlesZeroPerfectly()
    {
        Lua.DoString("local q = {[0] = 'a', [5] = 'b'} local n = 0 for k, v in pairs(q) do n = n + 1 end return n")
            .Number.ShouldBe(2);
    }
}
