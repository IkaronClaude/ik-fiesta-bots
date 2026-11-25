using Xunit;

namespace Fiesta.Bot.Next.Tests;

public class BlackboardTests
{
    private const string Brain = """
        brain: t
        api: 3
        modules: [brain/t/m]
        layers:
          - name: l
            priority: 50
            machine:
              initial: a
              states:
                a:
                  enter: m.write
                  transitions:
                    - { to: b, after: 100 }
                b:
                  enter: m.read
                  transitions:
                    - { to: a, after: 100000 }
        """;

    [Fact]
    public void Numeric_keys_of_a_blackboard_table_survive_state_changes()
    {
        var mods = new Dictionary<string, string>
        {
            ["brain/t/m"] = """
                local M = {}
                function M.write() if type(bb.bot.t) ~= "table" then bb.bot.t = {} end bb.bot.t[20000] = 7 end
                function M.read() bb.bot.got = bb.bot.t[20000] or -1 end
                return M
                """,
        };
        using var t = new TestBot();
        var b = t.Load(Brain, mods);
        t.Tick(10);
        Assert.Contains("\"got\":7", b.BlackboardJson().ToJsonString());
    }
}
