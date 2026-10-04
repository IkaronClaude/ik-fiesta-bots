using System;
using System.IO;
using System.Threading.Tasks;
using Fiesta.Bot.Login;
using Fiesta.Bot.Manager;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>
/// 2026-10-04: one GateBot spawn while the zones were down became 79,864 bots and ~10,000 sockets on the login port.
/// Relog() re-spawns from handle.Options; with the auto-id left out of them, every relog attempt minted a NEW bot (and
/// roster entry) and then waited for the old id. These pin the two invariants that stop it.
/// </summary>
public class BotSpawnIdentityTests
{
    private static BotSpawnOptions Options() => new()
    {
        Host = "127.0.0.1",
        LoginPort = 1,                                   // nothing listens: the login fails at once, nothing is reached
        Credentials = new BotCredentials("nobody", "00000000000000000000000000000000"),
    };

    private static BotManager Manager(string dir)
    {
        Environment.SetEnvironmentVariable("BOT_KNOWLEDGE_DIR", dir);
        try { return new BotManager(new byte[499]); }
        finally { Environment.SetEnvironmentVariable("BOT_KNOWLEDGE_DIR", null); }
    }

    private static string ScratchDir() => Directory.CreateTempSubdirectory("botspawn-").FullName;

    [Fact]
    public async Task An_auto_id_spawn_carries_its_id_in_its_own_options()
    {
        var manager = Manager(ScratchDir());

        var handle = manager.Spawn(Options());

        handle.Options.Id.ShouldBe(handle.Id);           // what Relog() and the roster replay re-spawn from
        await manager.StopAsync(handle.Id, default, forget: true);
    }

    [Fact]
    public async Task An_auto_id_skips_ids_the_saved_roster_already_holds()
    {
        var dir = ScratchDir();
        Manager(dir).Knowledge.SaveRosterEntry("b1", Options() with { Id = "b1" });   // a previous process's bot
        var manager = Manager(dir);                                                   // a fresh process: counter at 0

        var handle = manager.Spawn(Options());

        handle.Id.ShouldNotBe("b1");
        await manager.StopAsync(handle.Id, default, forget: true);
    }
}
