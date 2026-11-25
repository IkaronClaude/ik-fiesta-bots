using Fiesta.Bot.Brain;
using Fiesta.Bot.Protocol;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>The leveller brain is operator-private (scripts/ is not in git); these tests run when it is present.</summary>
public class LevelerBrainTests
{
    private static (string Yaml, Dictionary<string, string> Modules)? Load()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "scripts", "brains", "leveler"))) d = d.Parent;
        if (d is null) return null;
        var dir = Path.Combine(d.FullName, "scripts", "brains", "leveler");
        var mods = Directory.GetFiles(dir, "*.lua").ToDictionary(f => "brain/leveler/" + Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        return (File.ReadAllText(Path.Combine(dir, "brain.yaml")), mods);
    }

    [Fact]
    public void Lints_clean_and_runs_without_script_errors()
    {
        if (Load() is not var (yaml, mods)) return;
        Assert.DoesNotContain(BrainLinter.Lint(BrainLoader.Parse(yaml), mods), l => l.Severity == LintSeverity.Error);
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(40);
        Assert.Empty(b.Debug.Errors());
        // no quest data, no atlas: the quest layer falls back to grinding and keeps re-planning, never wedges
        Assert.Contains(b.Layers[2].ActivePath[0].Name, new[] { "plan", "grind" });
    }

    [Fact]
    public void Heals_when_hp_drops_and_fights_what_attacks_it()
    {
        if (Load() is not var (yaml, mods)) return;
        using var t = new TestBot();
        var b = t.Load(yaml, mods);
        t.Tick(5);
        t.Ev(InboundKind.SelfHp, a: 20);
        t.Tick(10);
        Assert.Equal("recover", b.Layers[0].ActivePath[0].Name);
        // a mob hits us: the combat layer takes it on
        t.Ev(InboundKind.EntityAppeared, h: 2000, a: (long)Fiesta.Bot.Agent.EntityKind.Mob, b: 1, x: 5050, y: 5000);
        t.Ev(InboundKind.Damage, h: 2000, h2: 1000, a: 5, b: 15);
        t.Tick(5);
        Assert.Equal("fight", b.Layers[1].ActivePath[0].Name);
        Assert.Empty(b.Debug.Errors());
    }
}
