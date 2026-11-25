using Fiesta.Bot.Brain;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

/// <summary>API_SPEC 13.10: entering a sub-graph at an inner state (G/s), leaving it for a sibling (^/t), and the lint.</summary>
public class SubgraphTests
{
    private const string Brain = """
        brain: t
        api: 3
        blackboard: { go: 0 }
        layers:
          - name: l
            priority: 50
            machine:
              initial: a
              states:
                a:
                  transitions:
                    - { to: "g/y", when: "bb.bot.go == 1" }
                    - { to: a, after: 100000 }
                g:
                  machine:
                    initial: x
                    states:
                      x:
                        transitions:
                          - { to: y, after: 100000 }
                      y:
                        transitions:
                          - { to: "^/b", when: "bb.bot.go == 2" }
                          - { to: x, after: 100000 }
                  transitions:
                    - { to: a, after: 100000 }
                b:
                  transitions:
                    - { to: a, after: 100000 }
        """;

    private static string Path(BrainInstance b) => string.Join("/", b.Layers[0].ActivePath.Select(s => s.Name));

    [Fact]
    public void Enters_a_subgraph_at_an_inner_state_and_leaves_it_for_a_sibling()
    {
        using var t = new TestBot();
        var b = t.Load(Brain);
        t.Tick(2);
        Assert.Equal("a", Path(b));
        b.Control(new Fiesta.Bot.Agent.ControlCommand(Fiesta.Bot.Agent.ControlKind.SetBlackboard) { Scope = "bot", Key = "go", Value = "1" }, t.Ctx);
        t.Tick(2);
        Assert.Equal("g/y", Path(b));   // not g/x, the sub-machine's initial
        b.Control(new Fiesta.Bot.Agent.ControlCommand(Fiesta.Bot.Agent.ControlKind.SetBlackboard) { Scope = "bot", Key = "go", Value = "2" }, t.Ctx);
        t.Tick(2);
        Assert.Equal("b", Path(b));     // out of the group, to the parent's b
    }

    [Fact]
    public void Lint_knows_the_subgraph_forms()
    {
        Assert.DoesNotContain(BrainLinter.Lint(BrainLoader.Parse(Brain), new Dictionary<string, string>()), l => l.Severity == LintSeverity.Error);
        var bad = BrainLoader.Parse("""
            brain: t
            api: 3
            machines:
              loop:
                initial: p
                states:
                  p:
                    use: loop
                    transitions:
                      - { to: "^/nowhere", after: 1000 }
            layers:
              - name: l
                priority: 50
                machine:
                  initial: a
                  states:
                    a:
                      transitions:
                        - { to: "g/zz", after: 1000 }
                        - { to: u, after: 1000 }
                    u:
                      use: loop
                    g:
                      machine:
                        initial: x
                        states:
                          x:
                            transitions:
                              - { to: x, after: 1000 }
            """);
        var codes = BrainLinter.Lint(bad, new Dictionary<string, string>()).Select(l => l.Code).ToList();
        Assert.Contains("TARGET_UNKNOWN", codes);       // g/zz
        Assert.Contains("SUBGRAPH_NO_EXIT", codes);     // g: no own transition, no inner exit
        Assert.Contains("EXIT_TARGET_UNKNOWN", codes);  // loop exits to ^/nowhere, the layer has no such state
        Assert.Contains("MACHINE_RECURSION", codes);    // loop uses loop
    }
}
