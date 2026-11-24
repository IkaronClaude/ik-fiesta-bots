using System.Text;

namespace Fiesta.Bot.LuaBench;

/// <summary>The Lua source every engine runs. `API.` is replaced per engine: `bot.` for the MoonSharp userdata
/// variant (how BotApi is exposed today), empty for the global-function variants.</summary>
public static class Scenario
{
    public const string Template = """
        bb = { ticks = 0, last = -1 }

        function isGoodHeal(s) return s % 3 == 0 end      -- a USER helper (subjective, tweakable)

        function calls_scalar(n)
          local s = 0
          for i = 1, n do s = s + API.hp() end
          return s
        end

        function calls_arg(n)
          local s = 0
          for i = 1, n do s = s + API.dist(i % 50) end
          return s
        end

        function iter_mobs(n)
          local s = 0
          for k = 1, n do
            for h in API.mob_next, nil, -1 do s = s + API.dist(h) end
          end
          return s
        end

        function table_mobs(n)
          local s = 0
          for k = 1, n do
            local t = API.mobs_table()
            for i = 1, #t do s = s + t[i].dist end
          end
          return s
        end

        function strings(n)
          local s = 0
          for i = 1, n do
            local m = API.map()
            API.log("tick " .. i .. " on " .. m)
            s = s + #m
          end
          return s
        end

        function bb_access(n)
          local s = 0
          for i = 1, n do
            bb.ticks = bb.ticks + 1
            bb.last = i
            s = s + bb.ticks + (bb.missing or 0)
          end
          return s
        end

        -- a representative decision tick: ~110 C# crossings, one string every 20 ticks, no tables built
        function decide(n)
          local r = 0
          for k = 1, n do
            local hpv = API.hp()
            local best, bestScore = -1, 1e18
            for h in API.mob_next, nil, -1 do
              local d = API.dist(h)
              if d < 900 then
                local score = d + API.mob_hp(h) * 0.1
                if score < bestScore then best, bestScore = h, score end
              end
            end
            local skill = -1
            for s = 1, 8 do
              if API.can_use(s, best) and not isGoodHeal(s) then skill = s break end
            end
            bb.ticks = bb.ticks + 1
            bb.last = best
            if bb.ticks % 20 == 0 then API.log("tick " .. bb.ticks .. " target " .. best) end
            r = r + best * 100 + skill + hpv * 0
          end
          return r
        end

        -- the same decision written the way level_quest reads mobs today (a table per call)
        function decide_table(n)
          local r = 0
          for k = 1, n do
            local hpv = API.hp()
            local best, bestScore = -1, 1e18
            local t = API.mobs_table()
            for i = 1, #t do
              local m = t[i]
              if m.dist < 900 then
                local score = m.dist + m.hp * 0.1
                if score < bestScore then best, bestScore = m.handle, score end
              end
            end
            local skill = -1
            for s = 1, 8 do
              if API.can_use(s, best) and not isGoodHeal(s) then skill = s break end
            end
            bb.ticks = bb.ticks + 1
            if bb.ticks % 20 == 0 then API.log("tick " .. bb.ticks .. " target " .. best) end
            r = r + best * 100 + skill + hpv * 0
          end
          return r
        end
        """;

    public static string For(string apiPrefix) => Template.Replace("API.", apiPrefix);

    /// <summary>A ~1,600-line module of 300 small functions, for the hot-reload measurement</summary>
    public static readonly string ReloadModule = BuildReloadModule(300);

    private static string BuildReloadModule(int fns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("local M = {}");
        for (int i = 0; i < fns; i++)
        {
            sb.AppendLine($"function M.f{i}(a, b)");
            sb.AppendLine($"  local x = (a or 0) * {i} + (b or 1)");
            sb.AppendLine($"  if x > {i * 3} then return x - {i} end");
            sb.AppendLine("  return x");
            sb.AppendLine("end");
        }
        sb.AppendLine("reloaded = M");
        return sb.ToString();
    }
}
