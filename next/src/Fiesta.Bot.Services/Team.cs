namespace Fiesta.Bot.Services;

/// <summary>One bot's request to the others on the host (e.g. money for soul stones): what, how much, where it waits,
/// and which bot took it on ("" nobody yet).</summary>
public sealed record TeamAsk(string Name, string Kind, long Amount, string Map, double X, double Y, long At, string Claimer);

/// <summary>A host-wide board where bots ask each other for help (the teamwork behind trades). One ask per character;
/// any tick thread reads and writes it (lock-free reads of immutable records, compare-and-swap claims). Asks expire.</summary>
public sealed class TeamBoard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TeamAsk> _asks = new(StringComparer.Ordinal);

    /// <summary>How long an ask lives without being posted again (ms).</summary>
    public long TtlMs { get; init; } = 15 * 60_000;

    /// <summary>Post (or refresh) the ask of a character; a refresh keeps its claimer.</summary>
    public void Ask(string name, string kind, long amount, string map, double x, double y, long now) =>
        _asks.AddOrUpdate(name, _ => new TeamAsk(name, kind, amount, map, x, y, now, ""),
            (_, old) => new TeamAsk(name, kind, amount, map, x, y, now, old.Kind == kind ? old.Claimer : ""));

    /// <summary>Withdraw a character's ask.</summary>
    public void Withdraw(string name) => _asks.TryRemove(name, out _);

    /// <summary>The live ask of a character, null none.</summary>
    public TeamAsk? Get(string name, long now) => _asks.TryGetValue(name, out var a) && now - a.At <= TtlMs ? a : null;

    /// <summary>The oldest live, unclaimed ask of a kind by another character whose amount is at most
    /// <paramref name="maxAmount"/>, or the one <paramref name="me"/> already claimed; null none.</summary>
    public TeamAsk? Next(string me, string kind, long maxAmount, long now)
    {
        TeamAsk? best = null;
        foreach (var a in _asks.Values)
        {
            if (a.Name == me || a.Kind != kind || now - a.At > TtlMs) continue;
            if (a.Claimer == me) return a;
            if (a.Claimer.Length == 0 && a.Amount <= maxAmount && (best is null || a.At < best.At)) best = a;
        }
        return best;
    }

    /// <summary>Take on an ask (true when it was unclaimed or already ours).</summary>
    public bool Claim(string asker, string me)
    {
        while (_asks.TryGetValue(asker, out var a))
        {
            if (a.Claimer == me) return true;
            if (a.Claimer.Length > 0) return false;
            if (_asks.TryUpdate(asker, a with { Claimer = me }, a)) return true;
        }
        return false;
    }

    /// <summary>Give an ask back (only its claimer can).</summary>
    public void Release(string asker, string me)
    {
        while (_asks.TryGetValue(asker, out var a) && a.Claimer == me)
            if (_asks.TryUpdate(asker, a with { Claimer = "" }, a)) return;
    }

    /// <summary>Every ask (for the host API).</summary>
    public IReadOnlyCollection<TeamAsk> All => _asks.Values.ToList();
}
