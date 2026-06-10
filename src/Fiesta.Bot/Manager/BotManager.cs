using System.Collections.Concurrent;
using Fiesta.Bot.Login;
using Fiesta.Bot.Session;
using Fiesta.Bot.Zone;

namespace Fiesta.Bot.Manager;

/// <summary>
/// Owns N bots in parallel, keyed by id. Each <see cref="Spawn"/> kicks off the
/// full login chain on a background task — Login → WM (with optional in-band
/// character creation + tutorial decline) → [1801] zone entry → a long-lived
/// <see cref="BotSession"/> that answers heartbeats until the bot is stopped.
/// The same orchestration <c>LoginTestCli</c> proved end-to-end, minus the hold
/// timer: a managed bot runs until <see cref="StopAsync"/> (or a server kick).
///
/// Spawn returns immediately with a <see cref="BotHandle"/>; callers poll the
/// handle's <see cref="BotHandle.Phase"/>/<see cref="BotHandle.Snapshot"/> for
/// progress. Thread-safe; the control API is the primary caller.
/// </summary>
public sealed class BotManager : IAsyncDisposable
{
    private readonly byte[] _xorTable;
    private readonly Action<string>? _globalLog;
    private readonly ConcurrentDictionary<string, BotHandle> _bots = new(StringComparer.OrdinalIgnoreCase);
    private int _seq;

    public BotManager(byte[] xorTable, Action<string>? globalLog = null)
    {
        _xorTable = xorTable;
        _globalLog = globalLog;
    }

    /// <summary>Start a bot. Non-blocking — the login chain runs in the background;
    /// watch the returned handle for progress. Throws only on a duplicate id.</summary>
    public BotHandle Spawn(BotSpawnOptions options)
    {
        var id = options.Id ?? $"b{Interlocked.Increment(ref _seq)}";
        var handle = new BotHandle(id, options);
        if (!_bots.TryAdd(id, handle))
            throw new InvalidOperationException($"a bot with id '{id}' already exists");

        handle.Log($"spawn requested: {options.Host}:{options.LoginPort} user='{options.Credentials.Username}'");
        handle.RunTask = Task.Run(() => RunBotAsync(handle));
        return handle;
    }

    public IReadOnlyList<BotHandle> List() => _bots.Values.OrderBy(b => b.Id).ToArray();

    public BotHandle? Get(string id) => _bots.TryGetValue(id, out var h) ? h : null;

    /// <summary>Signal a bot to stop and wait (briefly) for it to wind down.
    /// Returns false if no such bot. The handle is removed once stopped.</summary>
    public async Task<bool> StopAsync(string id, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return false;
        handle.Log("stop requested");
        if (handle.Phase is not (BotPhase.Stopped or BotPhase.Failed))
            handle.SetPhase(BotPhase.Stopping);
        handle.Cts.Cancel();
        if (handle.RunTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (TimeoutException) { handle.Log("stop: lifecycle task did not finish within 10s"); }
            catch (OperationCanceledException) { }
        }
        _bots.TryRemove(id, out _);
        handle.Cts.Dispose();
        return true;
    }

    private async Task RunBotAsync(BotHandle handle)
    {
        var opt = handle.Options;
        var ct = handle.Cts.Token;
        void Log(string m) { handle.Log(m); _globalLog?.Invoke($"[{handle.Id}] {m}"); }

        FiestaClientConnectionScope wm = default;
        try
        {
            var chain = new LoginChain(_xorTable, Log);

            handle.SetPhase(BotPhase.LoggingIn);
            var login = await chain.RunLoginAsync(
                new FiestaEndpoint(opt.Host, opt.LoginPort), opt.Credentials, opt.WorldNo, ct);
            var wmPort = login.WmAdvertised.Port == 0 ? opt.WmPortFallback : login.WmAdvertised.Port;
            var wmEp = new FiestaEndpoint(opt.Host, wmPort);

            handle.SetPhase(BotPhase.SelectingChar);
            var (wmResult, wmConn) = await chain.RunWmAsync(
                wmEp, opt.Credentials, login.Otp, opt.Slot, opt.CreateSpec, ct);
            wm = new FiestaClientConnectionScope(wmConn);

            if (wmResult.ZoneAdvertised is not { } zoneAdv || wmResult.Selected is not { } sel)
                throw new InvalidOperationException(
                    "account has no character to enter a zone (and no create spec)");
            handle.SetCharName(sel.Name);

            handle.SetPhase(BotPhase.EnteringZone);
            var zoneEntry = ZoneEntry.FromDataDir(_xorTable, Log, opt.DataDir);
            var zoneEp = new FiestaEndpoint(opt.Host, zoneAdv.Port);
            var zoneConn = await zoneEntry.EnterAsync(zoneEp, wmResult.WmHandle, sel.Name, ct);

            // In zone. Run a session on BOTH links — the WM connection keeps
            // receiving heartbeats while in zone and must answer them too, and it
            // has to stay open (the zone validates against a live WM session).
            await using var zoneSession = new BotSession(zoneConn, sel.Name, wmResult.WmHandle, zoneEp, Log);
            var wmSession = new BotSession(wmConn, sel.Name, wmResult.WmHandle, wmEp, Log);
            handle.ZoneSession = zoneSession;
            handle.SetPhase(BotPhase.InZone);
            Log($"*** {sel.Name} IN ZONE ({zoneEp}) — running until stopped ***");

            await Task.WhenAll(zoneSession.RunAsync(ct), wmSession.RunAsync(ct));

            // Both loops returned. Cancellation = a clean stop; anything else
            // (peer closed) is a kick/drop — still "stopped" from our side.
            handle.SetPhase(BotPhase.Stopped);
            Log($"sessions ended — zone: {zoneSession.State.DisconnectReason}, wm: {wmSession.State.DisconnectReason}");
        }
        catch (OperationCanceledException)
        {
            handle.SetPhase(BotPhase.Stopped);
            Log("stopped (cancelled before zone entry)");
        }
        catch (Exception ex)
        {
            handle.SetError($"{ex.GetType().Name}: {ex.Message}");
            handle.SetPhase(BotPhase.Failed);
            Log($"[FAIL] {handle.Error}");
        }
        finally
        {
            wm.Dispose(); // zoneConn is owned/disposed by the zoneSession's DisposeAsync
        }
    }

    public async ValueTask DisposeAsync()
    {
        var handles = _bots.Values.ToArray();
        foreach (var h in handles) h.Cts.Cancel();
        foreach (var h in handles)
        {
            if (h.RunTask is { } task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* best-effort shutdown */ }
            }
            h.Cts.Dispose();
        }
        _bots.Clear();
    }

    /// <summary>Disposes the WM connection exactly once, even if it was never set
    /// (failure before the WM phase). The zone connection is owned by its session.</summary>
    private readonly struct FiestaClientConnectionScope(Net.FiestaClientConnection? conn) : IDisposable
    {
        public void Dispose() => conn?.Dispose();
    }
}
