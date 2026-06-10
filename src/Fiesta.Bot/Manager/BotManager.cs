using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Session;
using Fiesta.Bot.Zone;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Enums;
using FiestaLibReloaded.Networking.Structs;

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
        var inZone = handle.Phase == BotPhase.InZone && handle.ZoneSession is { } zs0 && handle.WmSession is not null;
        if (handle.Phase is not (BotPhase.Stopped or BotPhase.Failed))
            handle.SetPhase(BotPhase.Stopping);

        if (inZone)
        {
            // Clean logout: send the quit frames (zone: LOGOUTREADY+quit, WM: quit),
            // then DON'T cancel — keep the sessions running so they answer heartbeats
            // through the server's ~10s logout countdown (combat-logout: needs ~10s
            // with no damage). The server closes both links when it completes, which
            // ends RunTask. Cancelling/closing mid-countdown aborts the logout and
            // leaves the char "online" → the next login is duplicate-kicked.
            using var logoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await handle.ZoneSession!.LogoutAsync(logoutReady: true, logoutCts.Token); } catch { }
            if (handle.WmSession is { } ws) try { await ws.LogoutAsync(logoutReady: false, logoutCts.Token); } catch { }

            if (handle.RunTask is { } t)
            {
                try { await t.WaitAsync(TimeSpan.FromSeconds(14), ct); } // ~10s timer + slack
                catch (TimeoutException) { handle.Log("clean logout didn't complete in 14s — forcing"); handle.Cts.Cancel(); }
                catch (OperationCanceledException) { }
            }
        }
        else
        {
            handle.Cts.Cancel();
        }

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

    /// <summary>Outcome of a manual in-zone action.</summary>
    public enum ActionResult { Sent, NotFound, NotInZone }

    /// <summary>Make a bot say <paramref name="text"/> in its zone (local chat).</summary>
    public Task<ActionResult> SayAsync(string id, string text, CancellationToken ct = default)
        => ActAsync(id, $"say: \"{text}\"", s => s.SendAsync(ChatCodec.BuildChatReq(text), ct));

    /// <summary>Whisper <paramref name="text"/> to the player named <paramref name="to"/>.</summary>
    public Task<ActionResult> WhisperAsync(string id, string to, string text, CancellationToken ct = default)
        => ActAsync(id, $"whisper {to}: \"{text}\"", s => s.SendAsync(ChatCodec.BuildWhisperReq(to, text), ct));

    // The real client's cast sequence (from Z:/Buff.pcapng): TARGET the handle
    // (BAT TargettingReq), switch to battle/cast mode (ACT ChangemodeReq=2), THEN
    // send the skill cast (SKILLBASH_OBJ_CAST_REQ). Firing a bare cast with no
    // preceding target is rejected by the zone with a LinkendClientCmd kick. The
    // first two have no FiestaLib opcode attribute, so build them by opcode.
    private static readonly ushort OpBatTarget =
        (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.TargettingReq);
    private static readonly ushort OpActChangeMode =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.ChangemodeReq);

    /// <summary>Cast a skill on a target zone handle, replaying the client's full
    /// target → battle-mode → cast sequence so the zone accepts it.</summary>
    public Task<ActionResult> CastAsync(string id, ushort skill, ushort target, CancellationToken ct = default)
        => ActAsync(id, $"cast skill {skill} on h={target} (target+mode+cast)", async s =>
        {
            await s.SendAsync(new FiestaPacket(OpBatTarget, new byte[] { (byte)target, (byte)(target >> 8) }), ct);
            await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);
            await s.SendAsync(new PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ { skill = skill, target = target }, ct);
        });

    /// <summary>Use an inventory item by slot (invenType: 0 = normal bag).</summary>
    public Task<ActionResult> UseItemAsync(string id, byte slot, byte invenType, CancellationToken ct = default)
        => ActAsync(id, $"use item slot={slot} type={invenType}",
            s => s.SendAsync(new PROTO_NC_ITEM_USE_REQ { invenslot = slot, invenType = invenType }, ct));

    /// <summary>Equip the inventory item at <paramref name="slot"/> (the server
    /// derives the target equipment slot from the item itself).</summary>
    public Task<ActionResult> EquipAsync(string id, byte slot, CancellationToken ct = default)
        => ActAsync(id, $"equip inventory slot {slot}",
            s => s.SendAsync(new PROTO_NC_ITEM_EQUIP_REQ { slot = slot }, ct));

    // Move/run (ACT MoverunCmd, 0x2019): 16 bytes = fromX,fromY,toX,toY (u32 LE).
    private static readonly ushort OpMoveRun =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.MoverunCmd);

    /// <summary>Walk a precomputed path: send one MoverunCmd per segment on a
    /// background task, paced to <paramref name="unitsPerSec"/> so the server
    /// accepts it as a normal walk. Returns immediately; the walk continues until
    /// done or the bot is stopped. (Speed is an estimate — tune against the live
    /// server.)</summary>
    public ActionResult WalkPath(string id, IReadOnlyList<(uint X, uint Y)> waypoints, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session) return ActionResult.NotInZone;
        if (waypoints.Count < 2) return ActionResult.Sent;
        var ct = handle.Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < waypoints.Count - 1 && !ct.IsCancellationRequested; i++)
                {
                    var (fx, fy) = waypoints[i];
                    var (tx, ty) = waypoints[i + 1];
                    var p = new byte[16];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), fx);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), fy);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), tx);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), ty);
                    await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                    var dist = Math.Sqrt(Math.Pow((double)tx - fx, 2) + Math.Pow((double)ty - fy, 2));
                    await Task.Delay((int)Math.Clamp(dist / unitsPerSec * 1000, 40, 5000), ct);
                }
                handle.Log($"walk-path done ({waypoints.Count} waypoints)");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { handle.Log($"walk-path error: {ex.Message}"); }
        }, ct);
        return ActionResult.Sent;
    }

    /// <summary>Walk from one map coordinate to another (one MoverunCmd step).</summary>
    public Task<ActionResult> WalkAsync(string id, uint fromX, uint fromY, uint toX, uint toY, CancellationToken ct = default)
        => ActAsync(id, $"walk ({fromX},{fromY})->({toX},{toY})", s =>
        {
            var p = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), fromX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), fromY);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), toX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), toY);
            return s.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
        });

    /// <summary>Issue a GM command (e.g. <c>&amp;levelup 46</c>, <c>&amp;makeitem SafeProtection01</c>).
    /// GM commands are routed through the chat channel — the server processes the
    /// <c>&amp;</c>/<c>$</c> prefix when the account has GM authority (nAuthID=9).</summary>
    public Task<ActionResult> GmAsync(string id, string command, CancellationToken ct = default)
        => ActAsync(id, $"gm: {command}", s => s.SendAsync(ChatCodec.BuildChatReq(command), ct));

    /// <summary>Shared plumbing for a manual action on an in-zone bot: resolve →
    /// guard phase → send → log. This is the seam the HTTP endpoints (and later an
    /// LLM/Lua controller) drive.</summary>
    private async Task<ActionResult> ActAsync(string id, string logLine, Func<BotSession, Task> send)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session)
            return ActionResult.NotInZone;
        await send(session);
        handle.Log(logLine);
        return ActionResult.Sent;
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
            await using var zoneSession = new BotSession(zoneConn, sel.Name, wmResult.WmHandle, zoneEp, Log,
                linkTag: "zone", logInbound: opt.LogInbound);
            var wmSession = new BotSession(wmConn, sel.Name, wmResult.WmHandle, wmEp, Log,
                linkTag: "wm", logInbound: opt.LogInbound);
            handle.ZoneSession = zoneSession;
            handle.WmSession = wmSession;

            // Perception model (nearby players + chat) is always on — cheap, and the
            // status/say surface and any behavior read from it. The buff behavior is
            // opt-in via spawn options.
            using var zoneView = new ZoneView(zoneSession, Log);
            handle.ZoneView = zoneView;
            using var buff = opt.Buff is { } buffCfg
                ? new BuffInTownBehavior(zoneSession, zoneView, buffCfg, Log, ct)
                : null;

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
