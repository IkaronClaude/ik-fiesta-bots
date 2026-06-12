using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Navigation;
using Fiesta.Bot.Pathfinding;
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

    /// <summary>Resolves a map short-name to its walkability grid (BYO, from
    /// <c>BLOCKINFO_DIR</c>). Set by the host so navigation actions (e.g. <see
    /// cref="Follow"/>) can pathfind around obstacles; null = no grid available,
    /// callers fall back to straight-line movement.</summary>
    public Func<string, BlockGrid?>? GridProvider { get; set; }

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

    /// <summary>Cast a skill on a target zone handle, replaying the client's
    /// target → battle-mode → (stop) → cast sequence so the zone accepts it.
    /// <paramref name="stopFirst"/> sends a STOP_REQ committing our current position
    /// right before the cast: an offensive SKILLBASH is rejected
    /// (NC_BAT_SKILLBASH_CAST_FAIL_ACK) if the server considers us moving, so a damage
    /// skill must STOP first — verified in CombatExtensive.pcapng (every accepted cast
    /// is preceded by STOP_REQ). A heal is exempt; it can be cast while walking, so it
    /// passes <c>stopFirst:false</c> to avoid halting a moving bot.</summary>
    public async Task<ActionResult> CastAsync(string id, ushort skill, ushort target, bool stopFirst = true, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        await s.SendAsync(new FiestaPacket(OpBatTarget, new byte[] { (byte)target, (byte)(target >> 8) }), ct);
        await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);
        // Offensive skills enforce UsableDegree — the target must be within our facing
        // arc — so we must FACE it before casting (else NC_BAT_SKILLBASH_CAST_FAIL_ACK).
        // There's no rotate packet; the client turns via MOVERUN, whose from->to vector
        // sets facing. Send a tiny MOVERUN toward the target (just enough to turn, capped
        // so a ranged caster never closes into melee), then STOP, then cast. Verified in
        // CombatExtensive.pcapng: every accepted cast is preceded by MOVERUN→target. Heal
        // passes stopFirst:false (self-cast needs no facing and can be cast while moving).
        if (stopFirst && handle.Position is { } pos && NpcPos(handle, target) is { } tp)
        {
            var dx = (double)tp.X - pos.X; var dy = (double)tp.Y - pos.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            uint faceX = pos.X, faceY = pos.Y;
            if (dist > 1)
            {
                var step = Math.Min(16.0, dist - 1); // enough to set facing; never overshoot
                faceX = (uint)Math.Round(pos.X + dx / dist * step);
                faceY = (uint)Math.Round(pos.Y + dy / dist * step);
            }
            var mv = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(0), pos.X);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(4), pos.Y);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(8), faceX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(12), faceY);
            await s.SendAsync(new FiestaPacket(OpMoveRun, mv), ct);
            var stop = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), faceX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), faceY);
            await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
            handle.SetPosition(faceX, faceY);
        }
        await s.SendAsync(new PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ { skill = skill, target = target }, ct);
        handle.Log($"cast skill {skill} on h={target} ({(stopFirst ? "target+mode+face+cast" : "target+mode+cast")})");
        return ActionResult.Sent;
    }

    /// <summary>Cast a heal skill on yourself (cast target = own handle). The client's
    /// "a heal lands on me even with an enemy targeted" is a client-side redirect, so
    /// the bot self-targets explicitly — otherwise the server heals the enemy/ally in
    /// the cast packet. Needs the self handle (from the [1802] login ack).</summary>
    public Task<ActionResult> HealSelfAsync(string id, ushort skill, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return Task.FromResult(ActionResult.NotFound);
        if (handle.SelfHandle is not { } self) return Task.FromResult(ActionResult.NotInZone);
        return CastAsync(id, skill, self, stopFirst: false, ct: ct); // heal castable while moving
    }

    /// <summary>Attack: cast a (damage) skill on <paramref name="target"/>, or the
    /// nearest non-gate mob in view when target is 0. Bare target→mode→cast — it does
    /// NOT engage auto-attack or move the bot into melee (that would be wrong for a
    /// ranged/AoE caster; a mage casts without ever auto-attacking). To melee-swing,
    /// call <see cref="AutoAttackAsync"/> separately.</summary>
    public Task<ActionResult> AttackAsync(string id, ushort skill, ushort target = 0, CancellationToken ct = default)
    {
        if (target == 0 && _bots.TryGetValue(id, out var h) && NearestMob(h) is { } m) target = m;
        if (target == 0) return Task.FromResult(ActionResult.NotFound);
        return CastAsync(id, skill, target, ct: ct); // damage: STOP-then-cast (default)
    }

    // STOP (ACT StopReq 0x2012): 8 bytes [x u32][y u32] — the position the char halts at.
    private static readonly ushort OpActStop =
        (ushort)(((int)ProtocolCommand.Act << 10) | (int)ActOpcode.StopReq);
    // BASHSTART / BASHSTOP (BAT 0x242B / 0x2432, empty): begin / end melee auto-attack on
    // the current target. While bashing the server streams SWING_START/SWING_DAMAGE
    // (0x2447/0x2448) until the mob dies or we BASHSTOP — verified in CombatExtensive.pcapng.
    private const ushort OpBatBashStart = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.BashstartCmd);
    private const ushort OpBatBashStop = (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.BashstopCmd);

    /// <summary>Begin auto-attacking (melee swings) a target, or the nearest mob if 0.
    /// Mirrors the real client (CombatExtensive.pcapng, "click once, many swings"):
    /// target → battle mode → a short MOVERUN toward the mob + STOP (close to melee
    /// range and FACE it — a swing is rejected otherwise) → BASHSTART, after which the
    /// server streams continuous swing/damage until the mob dies or
    /// <see cref="StopAttackAsync"/>. Auto-attack is inherently melee; skills are cast
    /// separately via <see cref="AttackAsync"/>/<see cref="CastAsync"/> and are NOT
    /// coupled to this (a mage never auto-attacks).</summary>
    public async Task<ActionResult> AutoAttackAsync(string id, ushort target = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        if (target == 0 && NearestMob(handle) is { } m) target = m;
        if (target == 0) return ActionResult.NotFound;

        await s.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)target, (byte)(target >> 8) }), ct);
        await s.SendAsync(new FiestaPacket(OpActChangeMode, new byte[] { 0x02 }), ct);

        // Close to melee range + face the mob: a final MOVERUN toward it then STOP. The
        // server validates a swing against position/facing, so this is what lets the
        // first bash land when we're near but not engaged.
        if (handle.Position is { } pos && NpcPos(handle, target) is { } tp)
        {
            var dx = (double)pos.X - tp.X; var dy = (double)pos.Y - tp.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            const double melee = 30.0; // stand this far from the mob (adjacent)
            uint standX = pos.X, standY = pos.Y;
            if (dist > 1)
            {
                standX = (uint)Math.Round(tp.X + dx / dist * Math.Min(dist, melee));
                standY = (uint)Math.Round(tp.Y + dy / dist * Math.Min(dist, melee));
            }
            var mv = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(0), pos.X);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(4), pos.Y);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(8), standX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(mv.AsSpan(12), standY);
            await s.SendAsync(new FiestaPacket(OpMoveRun, mv), ct);
            var stop = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(0), standX);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stop.AsSpan(4), standY);
            await s.SendAsync(new FiestaPacket(OpActStop, stop), ct);
            handle.SetPosition(standX, standY);
        }
        await s.SendAsync(new FiestaPacket(OpBatBashStart, Array.Empty<byte>()), ct);
        handle.Log($"auto-attack h={target}");
        return ActionResult.Sent;
    }

    /// <summary>Stop auto-attacking (BAT BashstopCmd).</summary>
    public Task<ActionResult> StopAttackAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "stop auto-attack", s => s.SendAsync(new FiestaPacket(OpBatBashStop, Array.Empty<byte>()), ct));

    /// <summary>Position of a nearby NPC/mob by zone handle (null if not in view).</summary>
    private static (uint X, uint Y)? NpcPos(BotHandle handle, ushort target)
    {
        if (handle.ZoneView is not { } view) return null;
        foreach (var n in view.NearbyNpcs) if (n.Handle == target) return (n.X, n.Y);
        return null;
    }

    /// <summary>Handle of the nearest non-gate mob/NPC to the bot (null if none in
    /// view). Out in the field this is a mob; near town it may be an NPC — pass an
    /// explicit target to <see cref="AttackAsync"/> when that matters.</summary>
    private static ushort? NearestMob(BotHandle handle)
    {
        if (handle.ZoneView is not { } view || handle.Position is not { } pos) return null;
        ushort? best = null; var bestD = double.MaxValue;
        foreach (var n in view.NearbyNpcs)
        {
            if (n.IsGate) continue;
            var d = Math.Pow((double)n.X - pos.X, 2) + Math.Pow((double)n.Y - pos.Y, 2);
            if (d < bestD) { bestD = d; best = n.Handle; }
        }
        return best;
    }

    // ── Targeting / follow (zone) ─────────────────────────────────────────────
    // Targeting and follow are zone-side. A party-tab target (F2–F5 in the client)
    // is just a BAT TargettingReq on the member's zone handle; untarget (Esc) is a
    // bare BAT UntargetReq. "Follow" has no dedicated packet at all — the client
    // targets the player then streams MoverunCmd toward their moving position, which
    // is why follow drops at a map change (verified in PartyFriendTarget.pcapng).
    private static readonly ushort OpBatUntarget =
        (ushort)(((int)ProtocolCommand.Bat << 10) | (int)BatOpcode.UntargetReq);

    /// <summary>Target a zone handle (e.g. a party member for buffing).</summary>
    public Task<ActionResult> TargetAsync(string id, ushort target, CancellationToken ct = default)
        => ActAsync(id, $"target h={target}",
            s => s.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)target, (byte)(target >> 8) }), ct));

    /// <summary>Clear the current target (Esc).</summary>
    public Task<ActionResult> UntargetAsync(string id, CancellationToken ct = default)
        => ActAsync(id, "untarget", s => s.SendAsync(new FiestaPacket(OpBatUntarget, Array.Empty<byte>()), ct));

    /// <summary>Resolve a nearby player by name (case-insensitive) to their zone handle.</summary>
    private static ushort? HandleForName(BotHandle handle, string name)
        => handle.ZoneView?.NearbyPlayers
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Handle;

    /// <summary>Follow a nearby player by name: target them, then chase by streaming
    /// MoverunCmd toward their live position (stopping <paramref name="followDist"/>
    /// world-units short), refreshed on a background loop until the target leaves
    /// view, the bot is stopped, or <see cref="StopFollow"/> is called. This mirrors
    /// the real client — there is no follow packet, so a map change ends the follow.</summary>
    public ActionResult Follow(string id, string targetName, double followDist = 60.0, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } session) return ActionResult.NotInZone;
        if (HandleForName(handle, targetName) is not { } h0) return ActionResult.NotFound;

        var followCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.FollowCts?.Cancel();
        handle.FollowCts = followCts;
        var ct = followCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.SendAsync(new FiestaPacket(OpBatTarget, new[] { (byte)h0, (byte)(h0 >> 8) }), ct);
                handle.Log($"follow: chasing {targetName} (h={h0})");
                (uint X, uint Y)? lastPlan = null;
                while (!ct.IsCancellationRequested)
                {
                    var target = handle.ZoneView?.NearbyPlayers
                        .FirstOrDefault(p => string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase));
                    if (target is null) { handle.Log($"follow: {targetName} left view — stopping"); break; }
                    if (handle.Position is { } pos)
                    {
                        double dx = (double)target.X - pos.X, dy = (double)target.Y - pos.Y;
                        var dist = Math.Sqrt(dx * dx + dy * dy);
                        // Re-plan when out of range and either the target moved enough
                        // since the last plan, or our last walk finished short (WalkCts
                        // cleared) — the latter closes the final gap when they stop.
                        var moved = lastPlan is not { } lp
                            || Math.Abs((double)target.X - lp.X) + Math.Abs((double)target.Y - lp.Y) > followDist
                            || handle.WalkCts is null;
                        if (dist > followDist && moved)
                        {
                            var grid = handle.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
                            if (grid is not null)
                            {
                                // Pathfind around obstacles (a straight chase snags on
                                // lanterns/walls → MOVEFAIL), then walk it via WalkPath
                                // (chunked + MOVEFAIL-aware), trimmed to stop short.
                                var path = PathFinder.FindPath(grid, pos.X, pos.Y, target.X, target.Y);
                                if (path.Count > 0)
                                {
                                    var wp = PathFinder.Simplify(path);
                                    int keep = wp.Count;
                                    while (keep > 1)
                                    {
                                        var (wx, wy) = wp[keep - 1];
                                        if (Math.Sqrt(Math.Pow((double)wx - target.X, 2) + Math.Pow((double)wy - target.Y, 2)) < followDist) keep--;
                                        else break;
                                    }
                                    if (keep >= 2) WalkPath(id, wp.Take(keep).ToList(), unitsPerSec);
                                    lastPlan = (target.X, target.Y);
                                }
                            }
                            else
                            {
                                // No grid available — one capped straight-line step.
                                var step = Math.Min(dist - followDist, MaxStepFor(unitsPerSec));
                                var nx = (uint)Math.Round(pos.X + dx / dist * step);
                                var ny = (uint)Math.Round(pos.Y + dy / dist * step);
                                var p = new byte[16];
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), pos.X);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), pos.Y);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), nx);
                                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), ny);
                                await session.SendAsync(new FiestaPacket(OpMoveRun, p), ct);
                                handle.SetPosition(nx, ny);
                            }
                        }
                    }
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { handle.Log("follow: stopped"); }
            catch (Exception ex) { handle.Log($"follow error: {ex.Message}"); }
            finally { if (ReferenceEquals(handle.FollowCts, followCts)) handle.FollowCts = null; followCts.Dispose(); }
        }, ct);
        return ActionResult.Sent;
    }

    /// <summary>Stop an in-progress <see cref="Follow"/> loop (no-op if not following).</summary>
    public ActionResult StopFollow(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        handle.FollowCts?.Cancel();
        return ActionResult.Sent;
    }

    // ── Party (WorldManager link) ─────────────────────────────────────────────
    // Party and friend traffic is WM-side, not zone-side (verified in
    // PartyFriendTarget.pcapng). An invite is NC_PARTY_JOIN_REQ {target Name5}; the
    // recipient answers a join-propose with ALLOW_ACK / REJECT_ACK carrying the
    // *inviter's* name (no typed struct for those two — build by opcode). Letting an
    // invite expire = simply not answering.
    private const ushort OpPartyAllow = (ushort)(((int)ProtocolCommand.Party << 10) | 4); // 0x3804
    private const ushort OpPartyReject = (ushort)(((int)ProtocolCommand.Party << 10) | 5); // 0x3805

    /// <summary>Invite <paramref name="targetName"/> to a party (WM link).</summary>
    public Task<ActionResult> PartyInviteAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"party invite {targetName}",
            s => s.SendAsync(new PROTO_NC_PARTY_JOIN_REQ { target = Name5Of(targetName) }, ct));

    /// <summary>Accept a pending party invite from <paramref name="inviterName"/>.</summary>
    public Task<ActionResult> PartyAcceptAsync(string id, string inviterName, CancellationToken ct = default)
        => WmActAsync(id, $"party accept {inviterName}",
            s => s.SendAsync(new FiestaPacket(OpPartyAllow, Name5Of(inviterName).n5_name), ct));

    /// <summary>Decline a pending party invite from <paramref name="inviterName"/>.</summary>
    public Task<ActionResult> PartyDeclineAsync(string id, string inviterName, CancellationToken ct = default)
        => WmActAsync(id, $"party decline {inviterName}",
            s => s.SendAsync(new FiestaPacket(OpPartyReject, Name5Of(inviterName).n5_name), ct));

    /// <summary>Send a line to party chat (WM link).</summary>
    public Task<ActionResult> PartyChatAsync(string id, string text, CancellationToken ct = default)
        => WmActAsync(id, $"party-chat: \"{text}\"", s => s.SendAsync(ChatCodec.BuildPartyChatReq(text), ct));

    // ── Friend list (WorldManager link) ───────────────────────────────────────
    // All friend structs carry [self charid Name5][other friendid Name5]; the confirm
    // adds a trailing accept byte (0x01 accept / 0x00 decline). Add and delete are
    // one-shot requests; responding to an incoming request is the CONFIRM_ACK.

    /// <summary>Send a friend request to <paramref name="targetName"/> (WM link).</summary>
    public Task<ActionResult> FriendAddAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"friend add {targetName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_SET_REQ { charid = Name5Of(self), friendid = Name5Of(targetName) }, ct));

    /// <summary>Answer an incoming friend request from <paramref name="requesterName"/>:
    /// <paramref name="accept"/> true = add, false = decline (WM link).</summary>
    public Task<ActionResult> FriendConfirmAsync(string id, string requesterName, bool accept, CancellationToken ct = default)
        => WmActAsync(id, $"friend {(accept ? "accept" : "decline")} {requesterName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_SET_CONFIRM_ACK
            {
                charid = Name5Of(self), friendid = Name5Of(requesterName), accept_friend = (byte)(accept ? 1 : 0)
            }, ct));

    /// <summary>Remove <paramref name="targetName"/> from the friend list (WM link).</summary>
    public Task<ActionResult> FriendDeleteAsync(string id, string targetName, CancellationToken ct = default)
        => WmActAsync(id, $"friend delete {targetName}", (s, self) =>
            s.SendAsync(new PROTO_NC_FRIEND_DEL_REQ { charid = Name5Of(self), friendid = Name5Of(targetName) }, ct));

    /// <summary>Build a 20-byte Name5 from a character name (ASCII, NUL-padded).</summary>
    private static Name5 Name5Of(string name)
    {
        var n5 = new Name5();
        var b = FiestaText.Encode(name);
        Array.Copy(b, n5.n5_name, Math.Min(b.Length, n5.n5_name.Length));
        return n5;
    }

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
    // SERVERMENU_ACK (Menu dept 0x0F, cmd 2): answers a server menu prompt (0x3C01).
    // An instance gate (EldPri01) asks "move to Collapsed Prison field?" — option 0 =
    // Yes (verified C->S in PartyFriendTarget.pcapng).
    private const ushort OpMenuServerMenuAck = (ushort)((0x0F << 10) | 2); // 0x3C02
    // MAP_LOGINCOMPLETE (Map dept 6, cmd 3): "finished loading — spawn me in-world".
    // Sent at initial zone entry (ZoneEntry) and AGAIN after an in-band LINKSAME warp:
    // the server holds back the new map's entity broadcasts until it sees this, so an
    // instance (EldPri01) stays silent/empty without it.
    private const ushort OpMapLoginComplete = (ushort)(((int)ProtocolCommand.Map << 10) | 3); // 0x1803

    /// <summary>Take a field gate by its NPC handle: target → NPC-click. If the gate
    /// opens a Yes/No confirm menu (0x3C01, e.g. instance gates like EldPri01), answer
    /// it with SERVERMENU_ACK option <paramref name="menuOption"/> (0 = Yes). For a
    /// multi-destination gate, pass <paramref name="destMap"/> to pick the map by name.
    /// The bot must be within the gate's range; the zone then drives the transition
    /// (see <see cref="ZoneView.MapChanged"/>).</summary>
    public async Task<ActionResult> UseGateAsync(string id, ushort gateHandle, string? destMap = null, byte menuOption = 0, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is not { } s) return ActionResult.NotInZone;
        var hb = new byte[] { (byte)gateHandle, (byte)(gateHandle >> 8) };
        var view = handle.ZoneView;

        // An instance gate confirms with a menu (0x3C01 "move to <field>?") before it
        // transitions you; a plain field gate transitions outright with no menu. The
        // gate also auto-opens that menu when you stand on its trigger — so if we
        // spawned on the gate the menu may already be open before we click. Answer an
        // already-open menu directly; otherwise target+click and poll ~3s for one.
        async Task AnswerMenu()
        {
            await s.SendAsync(new FiestaPacket(OpMenuServerMenuAck, new[] { menuOption }), ct);
            view?.ClearServerMenu();
            handle.Log($"gate confirm menu answered (option {menuOption})");
        }

        if (view?.ServerMenuOpen == true)
        {
            await AnswerMenu();
        }
        else
        {
            await s.SendAsync(new FiestaPacket(OpBatTarget, hb), ct);
            await s.SendAsync(new FiestaPacket(OpActNpcClick, hb), ct);
            for (var waited = 0; waited < 3000; waited += 150)
            {
                await Task.Delay(150, ct);
                if (view?.ServerMenuOpen == true) { await AnswerMenu(); break; }
            }
        }
        if (!string.IsNullOrWhiteSpace(destMap))
        {
            var name3 = new byte[12]; // Name3, ASCII, null-padded
            var bytes = System.Text.Encoding.ASCII.GetBytes(destMap);
            Array.Copy(bytes, name3, Math.Min(bytes.Length, name3.Length));
            await s.SendAsync(new FiestaPacket(OpMapMultyLinkSelect, name3), ct);
        }
        handle.Log($"use gate h={gateHandle}{(destMap is null ? "" : $" -> {destMap}")}");
        return ActionResult.Sent;
    }

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

    // ── Autonomous multi-map travel ───────────────────────────────────────────
    // Stop this far (world units) short of a gate before clicking it — a gate takes
    // effect within its range, no need to stand on the tile (verified Portals.pcapng).
    // If a click from range doesn't transition, we close to the exact coord and retry.
    private const double GateApproachDist = 60.0;

    /// <summary>Outcome of kicking off an autonomous <see cref="TravelTo"/>.</summary>
    public enum TravelResult { Started, NotFound, NotInZone, AlreadyThere, NoRoute }

    /// <summary>Plan and begin autonomous travel to <paramref name="destMap"/>: route
    /// over the learned gate graph (BFS), then for each hop pathfind to the gate, take
    /// it, wait for the map transition (in-band LINKSAME or cross-server LINKOTHER), and
    /// repeat — learning each map's id↔name and gates as it goes. Returns immediately
    /// with the planned route; the journey runs on a background task (watch the bot log
    /// and <see cref="BotHandle.CurrentMap"/>). Cancel with <see cref="StopTravel"/>.</summary>
    public (TravelResult Result, IReadOnlyList<GateEdge>? Route) TravelTo(string id, string destMap, double unitsPerSec = 120.0)
    {
        if (!_bots.TryGetValue(id, out var handle)) return (TravelResult.NotFound, null);
        if (handle.Phase != BotPhase.InZone || handle.ZoneSession is null) return (TravelResult.NotInZone, null);
        if (handle.CurrentMap is not { } from) return (TravelResult.NotInZone, null);
        if (string.Equals(from, destMap, StringComparison.OrdinalIgnoreCase))
            return (TravelResult.AlreadyThere, Array.Empty<GateEdge>());

        ObserveGates(id); // fold the bot's in-view gates into the graph before planning
        var route = Graph.Route(from, destMap);
        if (route is null || route.Count == 0) return (TravelResult.NoRoute, null);

        var travelCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.TravelCts?.Cancel();
        handle.TravelCts = travelCts;
        _ = Task.Run(() => RunTravelAsync(handle, route, unitsPerSec, travelCts), travelCts.Token);
        return (TravelResult.Started, route);
    }

    /// <summary>Stop an in-progress <see cref="TravelTo"/> (no-op if not travelling).
    /// Also aborts the current walk so the bot halts where it is.</summary>
    public ActionResult StopTravel(string id)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        handle.TravelCts?.Cancel();
        handle.WalkCts?.Cancel();
        return ActionResult.Sent;
    }

    private async Task RunTravelAsync(BotHandle handle, IReadOnlyList<GateEdge> route, double unitsPerSec, CancellationTokenSource travelCts)
    {
        var id = handle.Id;
        var ct = travelCts.Token;
        try
        {
            handle.Log($"[travel] start -> {route[^1].ToMap} via {route.Count} hop(s): " +
                       string.Join(" -> ", route.Select(e => e.ToMap)));
            for (int hop = 0; hop < route.Count; hop++)
            {
                if (ct.IsCancellationRequested) break;
                var edge = route[hop];
                var expected = edge.ToMap;

                // Resolve the live gate to this destination from the current view. Gates
                // arrive in the field-enter MOB briefinfo, so one should be present soon
                // after the map loads — wait briefly for it.
                if (!await WaitUntilAsync(() => GateTo(handle, expected) is not null, 8000, ct)
                    || GateTo(handle, expected) is not { } gate)
                {
                    handle.Log($"[travel] hop {hop + 1}/{route.Count}: no gate to '{expected}' in view — aborting");
                    return;
                }
                handle.Log($"[travel] hop {hop + 1}/{route.Count}: -> {expected} via gate h={gate.Handle} @({gate.X},{gate.Y})");

                // Walk to within range of the gate (pathfind around obstacles if a grid
                // is available; else a best-effort straight approach via WalkPath).
                await ApproachAsync(id, handle, gate.X, gate.Y, GateApproachDist, unitsPerSec, ct);

                // Take the gate and wait for the transition. Tell OnMapChanged the
                // destination we're heading into (PendingDestMap) so it resolves + learns
                // the real map name from the handoff's id deterministically — before the
                // cross-server reconnect re-reads it. If clicking from range doesn't fire
                // the gate, close onto the exact tile and retry once.
                handle.PendingDestMap = expected;
                var seqBefore = handle.MapChangeSeq;
                await UseGateAsync(id, gate.Handle, ct: ct);
                if (!await WaitUntilAsync(() => handle.MapChangeSeq > seqBefore, 6000, ct))
                {
                    handle.Log($"[travel] hop {hop + 1}: gate didn't fire from range — closing in and retrying");
                    await ApproachAsync(id, handle, gate.X, gate.Y, 0, unitsPerSec, ct);
                    await UseGateAsync(id, gate.Handle, ct: ct);
                    if (!await WaitUntilAsync(() => handle.MapChangeSeq > seqBefore, 8000, ct))
                    {
                        handle.Log($"[travel] hop {hop + 1}: no transition after retry — aborting");
                        handle.PendingDestMap = null;
                        return;
                    }
                }
                handle.PendingDestMap = null; // consumed by OnMapChanged

                // A cross-server hop re-logs in on a fresh connection — wait until we're
                // back in zone before the next hop. In-band LINKSAME stays InZone throughout.
                if (!await WaitUntilAsync(
                        () => handle.Phase == BotPhase.InZone && handle.ZoneSession is not null, 20000, ct))
                {
                    handle.Log($"[travel] hop {hop + 1}: didn't re-enter zone after handoff — aborting");
                    return;
                }
                handle.SetCurrentMap(expected); // belt-and-suspenders for the next hop's grid
                ObserveGates(id); // learn the new map's gates (next hop + future routing)
                handle.Log($"[travel] hop {hop + 1}/{route.Count}: arrived on {expected}");
            }
            handle.Log(ct.IsCancellationRequested ? "[travel] cancelled" : $"[travel] done — arrived on {route[^1].ToMap}");
        }
        catch (OperationCanceledException) { handle.Log("[travel] cancelled"); }
        catch (Exception ex) { handle.Log($"[travel] error: {ex.Message}"); }
        finally { if (ReferenceEquals(handle.TravelCts, travelCts)) handle.TravelCts = null; travelCts.Dispose(); }
    }

    /// <summary>The live in-view gate whose link destination is <paramref name="map"/>
    /// (case-insensitive), or null if none is currently visible.</summary>
    private static NearbyNpc? GateTo(BotHandle handle, string map)
        => handle.ZoneView?.NearbyNpcs.FirstOrDefault(
            n => n.IsGate && string.Equals(n.LinkMap, map, StringComparison.OrdinalIgnoreCase));

    /// <summary>Walk the bot to within <paramref name="stopShort"/> world-units of
    /// (<paramref name="tx"/>,<paramref name="ty"/>), pathfinding over the current map's
    /// grid when one is available (a straight WalkPath segment otherwise), then wait
    /// until it arrives, the walk ends, or a 30 s cap. Used to reach a gate before
    /// taking it; shared by the travel loop.</summary>
    private async Task ApproachAsync(string id, BotHandle handle, uint tx, uint ty, double stopShort, double unitsPerSec, CancellationToken ct)
    {
        if (handle.Position is not { } pos) return;
        var grid = handle.CurrentMap is { } map ? GridProvider?.Invoke(map) : null;
        IReadOnlyList<(uint X, uint Y)> wp;
        if (grid is not null)
        {
            var path = PathFinder.FindPath(grid, pos.X, pos.Y, tx, ty);
            wp = path.Count == 0
                ? new[] { (pos.X, pos.Y), (tx, ty) } // unreachable on the grid — try direct
                : PathFinder.Simplify(path);
        }
        else wp = new[] { (pos.X, pos.Y), (tx, ty) };

        // Trim trailing waypoints inside stopShort of the target so we halt short of the gate.
        if (stopShort > 0 && wp.Count > 2)
        {
            var keep = wp.Count;
            while (keep > 2 && Dist(wp[keep - 1], tx, ty) < stopShort) keep--;
            wp = wp.Take(keep).ToList();
        }
        WalkPath(id, wp, unitsPerSec);
        await WaitUntilAsync(
            () => (handle.Position is { } p && Dist((p.X, p.Y), tx, ty) <= Math.Max(stopShort, 24)) || handle.WalkCts is null,
            30000, ct);
    }

    private static double Dist((uint X, uint Y) a, uint x, uint y)
        => Math.Sqrt(Math.Pow((double)a.X - x, 2) + Math.Pow((double)a.Y - y, 2));

    /// <summary>Poll <paramref name="cond"/> until it's true or <paramref name="timeoutMs"/>
    /// elapses; returns the final state. Throws if <paramref name="ct"/> is cancelled.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> cond, int timeoutMs, CancellationToken ct, int pollMs = 150)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(pollMs, ct);
        }
        return cond();
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
    /// movement. 250u matches the real client: MovementTypes.pcapng shows a single
    /// far mouse-click expand into a stream of ~250u MoverunCmd segments (the client
    /// pathfinds, the server does not), each accepted by the server.</summary>
    private const double MaxMoveStep = 250.0;

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
        // Per-walk cancellation (linked to the bot's lifetime) so a MOVEFAIL can abort
        // just this walk. Replace/cancel any prior walk.
        var walkCts = CancellationTokenSource.CreateLinkedTokenSource(handle.Cts.Token);
        handle.WalkCts?.Cancel();
        handle.WalkCts = walkCts;
        var ct = walkCts.Token;
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
                    var subSteps = Math.Max(1, (int)Math.Ceiling(segDist / MaxStepFor(unitsPerSec)));
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
            catch (OperationCanceledException) { handle.Log("walk-path aborted (cancelled / move blocked)"); }
            catch (Exception ex) { handle.Log($"walk-path error: {ex.Message}"); }
            finally
            {
                if (ReferenceEquals(handle.WalkCts, walkCts)) handle.WalkCts = null;
                walkCts.Dispose();
            }
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

    /// <summary>Like <see cref="ActAsync"/> but sends on the bot's <b>WM</b> link
    /// (party / friend traffic is WorldManager-side). Requires the bot in zone with a
    /// live WM session.</summary>
    private async Task<ActionResult> WmActAsync(string id, string logLine, Func<BotSession, Task> send)
    {
        if (!_bots.TryGetValue(id, out var handle)) return ActionResult.NotFound;
        if (handle.Phase != BotPhase.InZone || handle.WmSession is not { } wm) return ActionResult.NotInZone;
        await send(wm);
        handle.Log(logLine);
        return ActionResult.Sent;
    }

    /// <summary>WM action variant that also passes the bot's own character name (the
    /// <c>charid</c> Name5 the friend structs require).</summary>
    private Task<ActionResult> WmActAsync(string id, string logLine, Func<BotSession, string, Task> send)
        => WmActAsync(id, logLine, s =>
        {
            var self = _bots.TryGetValue(id, out var h) ? h.CharName ?? "" : "";
            return send(s, self);
        });

    /// <summary>Max single-MoverunCmd distance for a given walk speed. Anchored to the
    /// real client's ~250u segments at the ~120u/s on-foot speed; scales up with speed
    /// so a mount (higher move speed → larger client segments) gets proportionally
    /// bigger steps and keeps the per-second packet rate roughly constant.</summary>
    private static double MaxStepFor(double unitsPerSec) => Math.Max(MaxMoveStep, unitsPerSec * (MaxMoveStep / 120.0));

    /// <summary>React to a gate / town-portal transition: advance the tracked
    /// position to the new spawn coord and update the current map name. For an in-band
    /// change that's all the bot needs (it's still on the same connection). A
    /// cross-server handoff additionally needs a reconnect to the carried endpoint —
    /// that lives in the travel orchestrator; here we just record it and log.</summary>
    private void OnMapChanged(BotHandle handle, MapHandoff h, Action<string> log)
    {
        handle.SetPosition(h.X, h.Y);
        handle.BumpMapChange(); // wake any travel loop waiting on a transition
        // Resolve the destination map name. The handoff carries only the map id, so:
        // prefer what the catalog already knows; else, if a travel hop told us the
        // destination it's heading into (PendingDestMap), use that AND learn id↔name so
        // future routing/grids resolve it; else fall back to a synthetic id label. Doing
        // this here (not in the travel loop) makes the name deterministic before the
        // cross-server reconnect re-reads it — no race over the synthetic label.
        var name = Catalog.NameFor(h.MapId);
        if (name is null && handle.PendingDestMap is { } pending)
        {
            name = pending;
            Catalog.Learn(h.MapId, pending);
        }
        handle.SetCurrentMap(name ?? $"map#{h.MapId}");
        log($"[nav] now on {name} (mapId={h.MapId}) at ({h.X},{h.Y})" +
            (h.IsCrossServer ? $" — cross-server handoff to {h.Ip}:{h.Port}, reconnecting" : " (in-band)"));

        // In-band LINKSAME: re-send MAP_LOGINCOMPLETE so the server spawns us into the
        // new map and starts broadcasting its entities (mobs/NPCs/players). Without it
        // the bot sits in the destination invisible to the world (no mob packets). The
        // cross-server path doesn't need this — it does a full re-login (which sends it).
        if (!h.IsCrossServer && handle.ZoneSession is { } zs)
        {
            _ = zs.SendAsync(new FiestaPacket(OpMapLoginComplete, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
            log($"[nav] >> MAP_LOGINCOMPLETE (0x{OpMapLoginComplete:X4}) to spawn into {name}");
        }
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
                if (entry.CharHandle is { } selfH) handle.SetSelfHandle(selfH);

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
                zoneView.MoveFailed += pos =>
                {
                    // Server rejected a move into an off-grid obstacle: resync to its
                    // truth and abort the current walk so we stop pushing into it.
                    handle.SetPosition(pos.X, pos.Y);
                    handle.WalkCts?.Cancel();
                    Log($"[nav] move blocked — resynced to ({pos.X},{pos.Y}), walk aborted");
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
