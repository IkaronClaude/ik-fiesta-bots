using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Navigation;
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

    /// <summary>The world map graph, learned by play (gates seen in each map) and
    /// shared across all bots — so one bot's exploration helps the rest route.</summary>
    public MapGraph Graph { get; } = new();

    /// <summary>Map id↔short-name resolver, learned from gate links and (optionally)
    /// seeded from a BYO MapInfo dump via <c>MAPINFO_PATH</c>.</summary>
    public MapCatalog Catalog { get; } = new();

    public BotManager(byte[] xorTable, Action<string>? globalLog = null)
    {
        _xorTable = xorTable;
        _globalLog = globalLog;
        var seeded = Catalog.LoadSeedFromEnv();
        if (seeded > 0) _globalLog?.Invoke($"[nav] MapCatalog seeded {seeded} maps from MAPINFO_PATH");
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

    // Town multi-select portal (from Portals.pcapng): target the portal NPC, click
    // it, then select a destination by its TownPortal-table index. Built by opcode —
    // NPCCLICK (Act cmd 10) and TOWNPORTAL_REQ (Map cmd 26) carry trivial payloads.
    private const ushort OpActNpcClick = (ushort)(((int)ProtocolCommand.Act << 10) | 10);     // 0x200A
    private const ushort OpMapTownPortal = (ushort)(((int)ProtocolCommand.Map << 10) | 26);   // 0x181A

    /// <summary>Use a town multi-select portal: target → click the portal NPC →
    /// select destination <paramref name="dest"/> (its <c>TownPortal</c> table index;
    /// e.g. in RouN group 0: 0=RouN,1=RouVal01,2=Eld). The bot must already be next
    /// to the portal NPC. The server then map-transitions the bot.</summary>
    public Task<ActionResult> TownPortalAsync(string id, ushort npcHandle, byte dest, CancellationToken ct = default)
        => ActAsync(id, $"town-portal via npc h={npcHandle} -> dest {dest}", async s =>
        {
            var hb = new byte[] { (byte)npcHandle, (byte)(npcHandle >> 8) };
            await s.SendAsync(new FiestaPacket(OpBatTarget, hb), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            await s.SendAsync(new FiestaPacket(OpMapTownPortal, new[] { dest }), ct);
        });

    // Field-gate link: a gate is an NPC (flagstate=1) with a handle. The client takes
    // it the same way it clicks any NPC — target it then NPCClick — and the zone
    // replies with the LOGOUT + LINKSAME/LINKOTHER transition; no need to walk onto
    // the tile (just be within the gate's range). For a multi-destination gate the
    // server first sends MULTY_LINK_CMD and the client picks a destination by map
    // name via MULTY_LINK_SELECT_REQ. (Verified C->S in Portals.pcapng, 2026-06-11.)
    private const ushort OpMapMultyLinkSelect = (ushort)(((int)ProtocolCommand.Map << 10) | 31); // 0x181F

    /// <summary>Take a field gate by its NPC handle: target → NPC-click, then (if
    /// <paramref name="destMap"/> is given, for a multi-destination gate) select the
    /// destination map by name. The bot must be within the gate's range. The zone
    /// then drives the map transition (see <see cref="ZoneView.MapChanged"/>).</summary>
    public Task<ActionResult> UseGateAsync(string id, ushort gateHandle, string? destMap = null, CancellationToken ct = default)
        => ActAsync(id, $"use gate h={gateHandle}{(destMap is null ? "" : $" -> {destMap}")}", async s =>
        {
            var hb = new byte[] { (byte)gateHandle, (byte)(gateHandle >> 8) };
            await s.SendAsync(new FiestaPacket(OpBatTarget, hb), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            if (!string.IsNullOrWhiteSpace(destMap))
            {
                var name3 = new byte[12]; // Name3, ASCII, null-padded
                var bytes = System.Text.Encoding.ASCII.GetBytes(destMap);
                Array.Copy(bytes, name3, Math.Min(bytes.Length, name3.Length));
                await s.SendAsync(new FiestaPacket(OpMapMultyLinkSelect, name3), ct);
            }
        });

    /// <summary>Snapshot the gates the bot currently sees into the shared
    /// <see cref="Graph"/> (auto-discovery): each in-view gate becomes an edge from
    /// the bot's current map to the gate's destination. No-op until the bot knows its
    /// current map and is in zone. Returns the number of gate edges observed.</summary>
    public int ObserveGates(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return 0;
        if (handle.CurrentMap is not { } fromMap || handle.ZoneView is not { } view) return 0;
        var n = 0;
        foreach (var gate in view.NearbyNpcs)
        {
            if (!gate.IsGate || string.IsNullOrWhiteSpace(gate.LinkMap)) continue;
            Graph.ObserveGate(fromMap, gate.LinkMap!, gate.X, gate.Y, gate.Handle);
            n++;
        }
        return n;
    }

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

    /// <summary>Max distance (world units) of a single MoverunCmd. The server rejects
    /// large jumps as teleport/anti-cheat — verified live (2026-06-11): a walk made of
    /// ~64u steps moved the character, but ~560–2536u straight segments (from
    /// path-simplification) did not move it server-side at all. So long segments are
    /// re-chunked into steps no bigger than this, mirroring how the real client streams
    /// movement.</summary>
    private const double MaxMoveStep = 140.0;

    /// <summary>Walk a precomputed path: stream MoverunCmd steps on a background task,
    /// paced to <paramref name="unitsPerSec"/> so the server accepts it as a normal
    /// walk. Long straight segments are sub-divided into <see cref="MaxMoveStep"/>-sized
    /// steps (the server won't move the character on an over-long jump). Returns
    /// immediately; the walk continues until done or the bot is stopped.</summary>
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
                var steps = 0;
                for (int i = 0; i < waypoints.Count - 1 && !ct.IsCancellationRequested; i++)
                {
                    var (fx, fy) = waypoints[i];
                    var (tx, ty) = waypoints[i + 1];
                    var segDist = Math.Sqrt(Math.Pow((double)tx - fx, 2) + Math.Pow((double)ty - fy, 2));
                    var subSteps = Math.Max(1, (int)Math.Ceiling(segDist / MaxMoveStep));
                    double cx = fx, cy = fy;
                    for (int k = 1; k <= subSteps && !ct.IsCancellationRequested; k++)
                    {
                        // Interpolate the next intermediate point along the segment.
                        var sx = (uint)Math.Round(fx + (tx - (double)fx) * k / subSteps);
                        var sy = (uint)Math.Round(fy + (ty - (double)fy) * k / subSteps);
                        var p = new byte[16];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), (uint)Math.Round(cx));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), (uint)Math.Round(cy));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), sx);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), sy);
                        await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                        handle.SetPosition(sx, sy); // advance tracked position as we walk
                        var stepDist = Math.Sqrt(Math.Pow(sx - cx, 2) + Math.Pow(sy - cy, 2));
                        cx = sx; cy = sy; steps++;
                        await Task.Delay((int)Math.Clamp(stepDist / unitsPerSec * 1000, 40, 2000), ct);
                    }
                }
                handle.Log($"walk-path done ({waypoints.Count} waypoints, {steps} move steps)");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { handle.Log($"walk-path error: {ex.Message}"); }
        }, ct);
        return ActionResult.Sent;
    }

    /// <summary>Walk from one map coordinate to another (one MoverunCmd step).</summary>
    public async Task<ActionResult> WalkAsync(string id, uint fromX, uint fromY, uint toX, uint toY, CancellationToken ct = default)
    {
        var result = await ActAsync(id, $"walk ({fromX},{fromY})->({toX},{toY})", s =>
        {
            var p = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), fromX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), fromY);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), toX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), toY);
            return s.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
        });
        if (result == ActionResult.Sent && _bots.TryGetValue(id, out var h)) h.SetPosition(toX, toY);
        return result;
    }

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

    /// <summary>React to a gate / town-portal transition: advance the tracked
    /// position to the new spawn coord and update the current map name. For an in-band
    /// change that's all the bot needs (it's still on the same connection). A
    /// cross-server handoff additionally needs a reconnect to the carried endpoint —
    /// that lives in the travel orchestrator; here we just record it and log.</summary>
    private void OnMapChanged(BotHandle handle, MapHandoff h, Action<string> log)
    {
        handle.SetPosition(h.X, h.Y);
        // Resolve the destination map name: the catalog learns id↔name as the bot
        // takes named gates; fall back to a synthetic id label if we've never seen it.
        var name = Catalog.NameFor(h.MapId) ?? $"map#{h.MapId}";
        handle.SetCurrentMap(name);
        log($"[nav] now on {name} (mapId={h.MapId}) at ({h.X},{h.Y})" +
            (h.IsCrossServer ? $" — cross-server handoff to {h.Ip}:{h.Port}, reconnecting" : " (in-band)"));
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

            var zoneEntry = ZoneEntry.FromDataDir(_xorTable, Log, opt.DataDir);

            // The WM link stays open for the bot's whole in-zone life and across any
            // cross-server handoffs (each zone validates against a live WM session),
            // so its read loop runs once, in the background, for the duration.
            var wmSession = new BotSession(wmConn, sel.Name, wmResult.WmHandle, wmEp, Log,
                linkTag: "wm", logInbound: opt.LogInbound);
            handle.WmSession = wmSession;
            var wmRun = wmSession.RunAsync(ct);

            // Zone (re-)entry loop. A normal stop/kick breaks out; a cross-server
            // handoff (NC_MAP_LINKOTHER) re-enters with the new endpoint + WM handle
            // the handoff carried. In-band changes (NC_MAP_LINKSAME) never reach here —
            // they're handled live on the same connection by OnMapChanged.
            var zoneEp = new FiestaEndpoint(opt.Host, zoneAdv.Port);
            var zoneWmHandle = wmResult.WmHandle;
            // The zone login ack has no map name, but the WM avatar list does
            // (PROTO_AVATARINFORMATION.loginmap) — that's the authoritative start map.
            // StartMap is only a fallback (e.g. for a just-created character).
            var currentMap = string.IsNullOrWhiteSpace(sel.LoginMap) ? opt.StartMap : sel.LoginMap;
            var firstEntry = true;
            while (true)
            {
                handle.SetPhase(BotPhase.EnteringZone);
                handle.ZoneSession = null; // no live zone link during (re)connect
                var entry = await zoneEntry.EnterAsync(zoneEp, zoneWmHandle, sel.Name, ct);
                var zoneConn = entry.Conn;
                if (entry.SpawnX is { } spx && entry.SpawnY is { } spy) handle.SetPosition(spx, spy);

                // Tripped to break THIS zone session when a cross-server handoff lands,
                // without disturbing the WM loop or the bot's overall cancellation.
                using var zoneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                MapHandoff? handoff = null;

                await using var zoneSession = new BotSession(zoneConn, sel.Name, zoneWmHandle, zoneEp, Log,
                    linkTag: "zone", logInbound: opt.LogInbound);
                handle.ZoneSession = zoneSession;

                // Perception model (nearby players + chat) is always on — cheap, and the
                // status/say surface and any behavior read from it. The buff behavior is
                // opt-in via spawn options.
                using var zoneView = new ZoneView(zoneSession, Log);
                handle.ZoneView = zoneView;
                handle.SetCurrentMap(currentMap);
                zoneView.MapChanged += h =>
                {
                    OnMapChanged(handle, h, Log);
                    if (h.IsCrossServer) { handoff = h; zoneCts.Cancel(); } // break to reconnect
                };
                using var buff = opt.Buff is { } buffCfg
                    ? new BuffInTownBehavior(zoneSession, zoneView, buffCfg, Log, ct)
                    : null;

                handle.SetPhase(BotPhase.InZone);
                Log(firstEntry
                    ? $"*** {sel.Name} IN ZONE ({zoneEp}) — running until stopped ***"
                    : $"*** {sel.Name} RE-ENTERED ZONE ({zoneEp}, {currentMap}) after cross-server handoff ***");
                firstEntry = false;

                await zoneSession.RunAsync(zoneCts.Token);

                // A captured cross-server handoff (and not a real stop) means reconnect
                // to the carried endpoint with its WM handle and re-enter the zone.
                if (handoff is { IsCrossServer: true } ho && ho.Ip is { } ip && !ct.IsCancellationRequested)
                {
                    zoneEp = new FiestaEndpoint(ip, ho.Port);
                    zoneWmHandle = ho.WmHandle;
                    currentMap = handle.CurrentMap ?? currentMap;
                    Log($"[nav] reconnecting to zone {zoneEp} (wm={zoneWmHandle}) for cross-server handoff");
                    continue;
                }

                Log($"zone session ended — {zoneSession.State.DisconnectReason}");
                break;
            }

            // The zone loop ended for real (stop/kick). Wind down the WM link too.
            try { await wmRun; } catch (OperationCanceledException) { }
            handle.SetPhase(BotPhase.Stopped);
            Log($"sessions ended — wm: {wmSession.State.DisconnectReason}");
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
