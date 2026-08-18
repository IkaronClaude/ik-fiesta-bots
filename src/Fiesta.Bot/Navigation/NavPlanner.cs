using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Navigation;

/// <summary>
/// Runs pathfinding OFF the Lua tick thread. Measured on FighterFresh 2026-08-18, a single blocking
/// PathFinder.FindPath cost a median 1.6s and a worst case 25s, which is the whole reason the tick
/// ran at 0.5/sec: walkTo() was the tick. The search itself is unchanged -- it just no longer runs
/// where the script is waiting for it.
///
/// One worker per bot, LATEST TARGET WINS: a search already running is left to finish (FindPath has
/// no cancellation and is not ours to change), but its result is discarded if the target moved on,
/// and the newest target is searched immediately after. That bounds the work to one search at a
/// time per bot no matter how often the script calls walkTo.
/// </summary>
public sealed class NavPlanner
{
    /// <summary>What the last COMPLETED search for a given target concluded.</summary>
    public enum Verdict { Pending, Routed, Unreachable }

    private readonly object _gate = new();
    private (string Map, uint X, uint Y)? _wanted;   // newest target the script asked for
    private (string Map, uint X, uint Y)? _running;  // target the worker is searching right now
    private (string Map, uint X, uint Y)? _lastDone;
    private Verdict _lastVerdict = Verdict.Pending;
    private bool _workerLive;
    private long _lastStartTicks;

    /// <summary>Floor on how often the SAME target is re-searched. The blocking walkTo throttled itself -- the
    /// caller could not ask again until the search returned. Off the tick that brake is gone, and a bot that
    /// cannot move re-asks every few hundred ms, so a 1-core container would sit at 100% in A* forever.
    /// A different target is never delayed by this.</summary>
    private const int MinRepeatMs = 400;

    private readonly Func<(string Map, uint X, uint Y), bool> _search;

    /// <summary>runs one search+walk and reports whether a route was issued. Called on a worker thread.</summary>
    public NavPlanner(Func<(string Map, uint X, uint Y), bool> search) => _search = search;

    /// <summary>Searches in flight or queued right now (0, 1 or 2 -- never more).</summary>
    public bool Busy { get { lock (_gate) return _running is not null || _wanted is not null; } }

    /// <summary>The target the worker is searching, for logging.</summary>
    public (string Map, uint X, uint Y)? InFlight { get { lock (_gate) return _running; } }

    /// <summary>Ask for a route to (map,x,y) and return WITHOUT searching. The verdict is whatever the last
    /// completed search for this exact target concluded -- Pending until one completes.
    /// EVERY request re-searches. The cache answers the CALLER; it never suppresses the work, because the
    /// script re-calls walkTo precisely when it has stopped moving and wants the route re-issued -- answering
    /// "already routed" and doing nothing would leave the bot standing in a field, which is always a bug.</summary>
    public Verdict Request(string map, uint x, uint y)
    {
        var key = (map, x, y);
        Verdict answer;
        lock (_gate)
        {
            answer = _lastDone == key ? _lastVerdict : Verdict.Pending;
            _wanted = key;
            if (_workerLive) return answer;
            _workerLive = true;
        }
        _ = Task.Run(Drain);
        return answer;
    }

    /// <summary>Forget the cached verdict, so the next Request re-searches even for the same target.
    /// Used when the world changed under us (map change, MOVEFAIL learned a new block).</summary>
    public void Invalidate()
    {
        lock (_gate) { _lastDone = null; _lastVerdict = Verdict.Pending; }
    }

    private void Drain()
    {
        try
        {
            while (true)
            {
                (string Map, uint X, uint Y) key;
                lock (_gate)
                {
                    if (_wanted is not { } w) { _workerLive = false; return; }
                    key = w; _wanted = null; _running = key;
                }
                var sinceLast = Environment.TickCount64 - _lastStartTicks;
                if (_lastDone == key && sinceLast < MinRepeatMs) Thread.Sleep((int)(MinRepeatMs - sinceLast));
                _lastStartTicks = Environment.TickCount64;
                var routed = false;
                try { routed = _search(key); }
                catch { routed = false; }
                lock (_gate)
                {
                    _running = null;
                    // Publish unless the TARGET changed while we searched -- then this answer describes a place
                    // we are no longer going. A repeat request for the SAME target must still be answered, or
                    // "unreachable" could never surface to a caller that keeps asking.
                    if (_wanted is null || _wanted == key)
                    { _lastDone = key; _lastVerdict = routed ? Verdict.Routed : Verdict.Unreachable; }
                }
            }
        }
        catch { lock (_gate) _workerLive = false; }
    }
}
