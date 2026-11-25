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

    [Fact]
    public void Numeric_keys_of_a_blackboard_table_survive_a_hot_swap()
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
        t.Tick(3);
        var cp = b.Checkpoint();
        // a new instance restores bb.bot from its JSON (what a resume and a hot swap do), then only reads
        var mods2 = new Dictionary<string, string> { ["brain/t/m"] = mods["brain/t/m"].Replace("bb.bot.t[20000] = 7", "") };
        var b2 = t.Load(Brain, mods2);
        b2.Resume(cp);
        t.Tick(10);
        Assert.Contains("\"got\":7", b2.BlackboardJson().ToJsonString());
    }

    [Fact]
    public void Brain_time_is_wall_time_so_stored_times_survive_a_host_restart()
    {
        var mods = new Dictionary<string, string>
        {
            ["brain/t/m"] = """
                local M = {}
                function M.write() bb.bot.at = time.now() end
                function M.read() bb.bot.age = time.since(bb.bot.at) end
                return M
                """,
        };
        using var t = new TestBot();
        var b = t.Load(Brain, mods);
        t.Tick(10);
        var bot = b.BlackboardJson()["bot"]!;
        Assert.True(bot["at"]!.GetValue<long>() > 1_600_000_000_000L);   // Unix ms, not process-relative
        Assert.InRange(bot["age"]!.GetValue<long>(), 0, 1000);
    }
}
