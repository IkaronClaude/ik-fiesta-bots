using Fiesta.Bot.Manager;
using Fiesta.Bot.Session;

namespace Fiesta.Bot.Behaviors;

/// <summary>Picks up drops that are ALREADY WITHIN REACH, on its own clock, regardless of what the driver script
/// happens to be doing.
///
/// WHY THIS IS NOT IN THE LUA: looting there is a PHASE, so it only runs when the driver is in a phase that loots.
/// Operator: "it does loot but it seems to lock on something and the loot commands aren't active in all phases...
/// it'd be reasonable that during a kite we do not walk away to loot, but we SHOULD loot things in range." Both
/// halves of that are right, and they are different jobs:
///   - WALKING to a drop is a decision that competes with fighting, fleeing and travelling. It belongs to the driver,
///     which is the only thing that knows whether walking is currently a good idea.
///   - PICKING UP something you are already standing on costs nothing, competes with nothing, and there is no state
///     in which it is the wrong move. That is a reflex, not a decision, so it lives here — the same shape as party
///     invite handling, which also must not depend on the driver being in an agreeable mood.
///
/// It therefore NEVER moves the bot. If the item is out of reach it is left for the driver.</summary>
public sealed class AutoLootBehavior : IDisposable
{
    private readonly BotHandle _handle;
    private readonly ZoneView _view;
    private readonly Func<ushort, Task> _pickup;
    private readonly Action<BotLogLevel, string> _log;
    private readonly CancellationTokenSource _cts;

    /// <summary>Reach for a pick with NO walking. The server enforces its own range; this only avoids asking for
    /// things that are obviously too far, and it is deliberately short — anything further is the driver's call.</summary>
    private const double LootRange = 60.0;

    /// <summary>How often to sweep. Drops that appear while we are mid-fight raise DropAppeared, but items already on
    /// the ground when we arrive raise nothing, so a poll is needed as well as the event.</summary>
    private const int SweepMs = 250;

    public AutoLootBehavior(BotHandle handle, ZoneView view, Func<ushort, Task> pickup,
        Action<BotLogLevel, string> log, CancellationToken ct)
    {
        _handle = handle;
        _view = view;
        _pickup = pickup;
        _log = log;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _view.DropAppeared += OnDrop;
        _ = Task.Run(SweepAsync, _cts.Token);
        _log(BotLogLevel.Info, $"[loot] auto-loot active — anything within {LootRange:F0}u is picked up on sight, " +
                               "in any phase, without moving the bot");
    }

    private void OnDrop(GroundItem _) => TryPick();

    private async Task SweepAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TryPick();
                await Task.Delay(SweepMs, _cts.Token);
            }
        }
        catch (OperationCanceledException) { /* session went away */ }
        catch (Exception ex) { _log(BotLogLevel.Note, $"[loot] auto-loot stopped: {ex.Message}"); }
    }

    private void TryPick()
    {
        // CanPick is the server's one-at-a-time pick gate, shared with the driver so the two cannot double-send.
        if (!_view.CanPick || _view.BagFull) return;
        // A full bag is not "skip this item", it is a state the driver has to resolve (sell/storage); picking would
        // just fail with 0x346 and burn the gate.
        if (_view.BagFreeSlots <= 0) return;
        if (_handle.Position is not { } pos) return;

        ushort best = 0;
        var bestDist = LootRange;
        foreach (var d in _view.Drops)
        {
            var dist = Math.Sqrt(Math.Pow((double)d.X - pos.X, 2) + Math.Pow((double)d.Y - pos.Y, 2));
            if (dist > bestDist) continue;
            bestDist = dist; best = d.Handle;
        }
        if (best == 0) return;
        _log(BotLogLevel.Verbose, $"[loot] auto-pick h={best} at {bestDist:F0}u (in reach, no walking)");
        _ = _pickup(best);
    }

    public void Dispose()
    {
        _view.DropAppeared -= OnDrop;
        _cts.Cancel();
        _cts.Dispose();
    }
}
