using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Fiesta.Bot.Manager;

namespace Fiesta.Bot.Host;

/// <summary>GET /api/bots/{id}/stream — a WebSocket stream of NDJSON: one parsed, friendly JSON document per message, push…</summary>
public static class EventStreamEndpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How often SELF is re-checked and re-sent if it changed</summary>
    private const int SelfSampleMs = 200;

    public static void MapEventStream(this WebApplication app, BotManager? manager)
    {
        app.Map("/api/bots/{id}/stream", async (HttpContext ctx, string id) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "This endpoint is a WebSocket. Connect with ws(s)://.../api/bots/{id}/stream",
                });
                return;
            }
            var bot = manager?.Get(id);
            if (bot is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // ECHO A SUBPROTOCOL WHEN ONE WAS OFFERED
            var offered = ctx.WebSockets.WebSocketRequestedProtocols;
            using var ws = offered.Contains("bearer")
                ? await ctx.WebSockets.AcceptWebSocketAsync("bearer")
                : await ctx.WebSockets.AcceptWebSocketAsync();
            await PumpAsync(ws, bot, manager!, ctx.RequestAborted);
        })
        .WithTags("Bots")
        .WithSummary("WebSocket NDJSON stream of parsed game events — per-entity updates as they arrive");
    }

    private static object FullState(BotHandle bot, BotManager manager, string kind) => new
    {
        t = kind,
        id = bot.Id,
        mapDisplay = manager.ClientData?.MapDisplayName(bot.CurrentMap),
        // The SAME shape /entities returns, so the page has one draw path and hello can be assigned straight into it
        entities = BotEndpoints.EntityPanel(bot, manager.ClientData),
        maxHp = bot.ZoneView?.MaxHp ?? 0,
    };

    private static async Task PumpAsync(WebSocket ws, BotHandle bot, BotManager manager, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;

        // Dirty set + signal. A handle already pending collapses into the same update (see the class doc)
        var dirty = new HashSet<ushort>();
        var gone = new HashSet<ushort>();
        var wake = new SemaphoreSlim(0, 1);
        // Out-of-band one-shot messages (chat, hits, map changes) that are NOT per-entity state and must not be collapse…
        var oob = Channel.CreateBounded<object>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        void Signal()
        {
            if (wake.CurrentCount == 0) { try { wake.Release(); } catch (SemaphoreFullException) { } }
        }
        void MarkDirty(ushort h) { lock (dirty) { dirty.Add(h); gone.Remove(h); } Signal(); }
        void MarkGone(ushort h) { lock (dirty) { gone.Add(h); dirty.Remove(h); } Signal(); }
        void Post(object o) { oob.Writer.TryWrite(o); Signal(); }
        var resendState = false;   // set by a map change; drained by the pump (see FullState)

        // SUBSCRIBE TO THE *BOT*, NOT THE ZoneView OBJECT
        Session.ZoneView? attached = null;
        void AttachZoneView(Session.ZoneView? zv)
        {
            if (ReferenceEquals(zv, attached)) return;
            if (attached is not null)
            {
                attached.EntityChanged -= MarkDirty;
                attached.EntityGone -= MarkGone;
            }
            attached = zv;
            if (attached is not null)
            {
                attached.EntityChanged += MarkDirty;
                attached.EntityGone += MarkGone;
            }
        }

        void OnBotEvent(BotEvent e)
        {
            switch (e.Kind)
            {
                case BotEventKind.Chat when e.Data is Session.ChatMessage c:
                    Post(new { t = "chat", from = c.SenderName, text = c.Text }); break;
                case BotEventKind.CastFail when e.Data is ushort code:
                    Post(new { t = "castfail", code, codeHex = $"0x{code:X4}" }); break;
                case BotEventKind.Hit when e.Data is Session.HitInfo h:
                    // The single most useful thing on a combat map: WHO hit WHOM for how much, as it lands
                    Post(new { t = "hit", attacker = (int)h.Attacker, defender = (int)h.Defender, dmg = (int)h.Damage, restHp = h.RestHp });
                    break;
                // READ THE MAP FROM THE HANDOFF, NOT FROM bot.CurrentMap
                case BotEventKind.MapChanged when e.Data is Navigation.MapHandoff mh:
                {
                    var name = manager.ClientData?.MapName(mh.MapId) ?? bot.CurrentMap;
                    Post(new
                    {
                        t = "map",
                        mapId = mh.MapId,
                        map = name,
                        mapDisplay = manager.ClientData?.MapDisplayName(name),
                        x = mh.X, y = mh.Y,
                    });
                    // AND RE-SEND THE WHOLE STATE
                    resendState = true; Signal();
                    break;
                }
                // VITALS AS EVENTS, not as a 2-second poll. HPCHANGE/SPCHANGE already arrive on the wire and the
                // handle already republishes them; the stream simply never forwarded them, so the UI's health bar
                // moved on a /metrics poll instead of on the packet that changed it.
                case BotEventKind.Hp when e.Data is uint hpNow:
                    Post(new { t = "hp", hp = (int)hpNow, maxHp = bot.ZoneView?.MaxHp ?? 0 }); break;
                case BotEventKind.Sp when e.Data is uint spNow:
                    Post(new { t = "sp", sp = (int)spNow, maxSp = bot.ZoneView?.MaxSp ?? 0 }); break;
                case BotEventKind.SkillLearned when e.Data is Manager.SkillLearnedInfo sl:
                    Post(new
                    {
                        t = "skilllearn",
                        skillId = sl.SkillId,
                        level = sl.Level,
                        passive = sl.Passive,
                        // Resolve the name here: the page should not have to own a copy of the skill tables, and the
                        // id alone is unreadable in a feed. ACTIVE and PASSIVE are separate, OVERLAPPING id spaces --
                        // id 5 is a different skill in each -- so the flag picks the table, it is not cosmetic.
                        name = sl.Passive ? manager.ClientData?.PassiveSkillName(sl.SkillId)
                                          : manager.ClientData?.SkillName(sl.SkillId),
                    });
                    break;
                case BotEventKind.SkillCast when e.Data is Manager.SkillCastInfo sc:
                    Post(new
                    {
                        t = "skillcast",
                        skillId = sc.SkillId,
                        target = sc.Target,
                        name = manager.ClientData?.SkillName(sc.SkillId),   // a cast is always an ACTIVE skill
                    });
                    break;
                case BotEventKind.Stones:
                {
                    // Same shape the /metrics poll builds, so the page reuses stoneTiles() unchanged. Built here
                    // from live ZoneView state rather than carried on the event: the reserve is four numbers plus
                    // two cooldowns, and freezing that into an event signature would date badly.
                    var zvs = bot.ZoneView;
                    Post(new
                    {
                        t = "stones",
                        stones = new object[]
                        {
                            new
                            {
                                kind = "hp", count = zvs?.HpStones, max = zvs?.MaxHpStones ?? 0,
                                cooldownMs = zvs?.HpStoneCooldownMs,
                                // ReadyInMs is -1 for "no successful use yet", which with a known cooldown means
                                // READY -- clamp rather than letting -1 reach the sweep as a negative fraction.
                                remainingMs = zvs is null ? (double?)null : Math.Max(0, zvs.HpStoneReadyInMs),
                                depleted = zvs?.HpStoneDepleted ?? false,
                            },
                            new
                            {
                                kind = "sp", count = zvs?.SpStones, max = zvs?.MaxSpStones ?? 0,
                                cooldownMs = zvs?.SpStoneCooldownMs,
                                remainingMs = zvs is null ? (double?)null : Math.Max(0, zvs.SpStoneReadyInMs),
                                depleted = zvs?.SpStoneDepleted ?? false,
                            },
                        },
                    });
                    break;
                }
                case BotEventKind.Exp when e.Data is long expNow:
                    Post(new { t = "exp", exp = expNow }); break;
                case BotEventKind.Money when e.Data is long cenNow:
                    Post(new { t = "money", money = cenNow }); break;
                case BotEventKind.LevelUp when e.Data is byte lvNow:
                    // A level-up invalidates most of the panel at once (stats, exp band, what is deprioritized),
                    // so it also asks for a full resend rather than trying to patch each field.
                    Post(new { t = "levelup", level = (int)lvNow });
                    resendState = true; Signal();
                    break;
                case BotEventKind.MoveFailed when e.Data is ValueTuple<uint, uint> p:
                    Post(new { t = "movefail", x = p.Item1, y = p.Item2 }); break;
                case BotEventKind.PlayerLeft when e.Data is ushort h2:
                    MarkGone(h2); break;
            }
        }

        bot.Events += OnBotEvent;
        AttachZoneView(bot.ZoneView);
        try
        {
            await SendAsync(ws, FullState(bot, manager, "hello"), ct);

            object? lastSelf = null;
            var lastSelfJson = "";
            // Drain the client side so a close frame is noticed promptly; we never expect inbound data
            _ = Task.Run(async () =>
            {
                var buf = new byte[256];
                try
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(buf, ct);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch { /* socket closed */ }
                finally { cts.Cancel(); }
            }, ct);

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                AttachZoneView(bot.ZoneView);      // survive a map handoff (see AttachZoneView)

                // ONE-SHOT EVENTS BEFORE THE RESEND, and the order matters more than it looks.
                // A map change queues BOTH a t:"map" here and resendState below. Draining second put "state" on the
                // wire first, and the client's map handler clears mobs/npcs -- so it wiped the full state that had
                // just arrived. FullState -> EntityPanel is the ONLY path that carries the whole-map NPC seed (the
                // per-entity path walks NearbyNpcs, which is AoI-only), so the gate and NPC layer stayed EMPTY after
                // every map change until the socket reconnected. Operator, watching it live: "Bot just arrived in
                // Burning Hill WITHOUT NPC (gate) SEED! npcs are only filling in as we get near."
                while (oob.Reader.TryRead(out var msg)) await SendAsync(ws, msg, ct);

                if (resendState)
                {
                    resendState = false;
                    lock (dirty) { dirty.Clear(); gone.Clear(); }   // they named the OLD map's handles
                    await SendAsync(ws, FullState(bot, manager, "state"), ct);
                }

                // Then the coalesced entity state
                ushort[] changed, removed;
                lock (dirty)
                {
                    changed = [.. dirty]; dirty.Clear();
                    removed = [.. gone]; gone.Clear();
                }
                if (changed.Length > 0)
                {
                    var cd = manager.ClientData;
                    var aggro = new HashSet<ushort>(bot.ZoneView?.Aggressors ?? []);
                    var questMobs = BotEndpoints.QuestMobIds(bot, cd);
                    // Index once per drain rather than scanning NearbyNpcs per handle — a map-enter burst dirties every entity on th…
                    var live = bot.ZoneView?.NearbyNpcs.ToDictionary(n => n.Handle) ?? [];
                    foreach (var h in changed)
                    {
                        if (!live.TryGetValue(h, out var n)) continue;
                        // MOB LAYER first; an entity that is not one is offered to the NPC layer
                        if (BotEndpoints.MobView(bot, cd, n, aggro, questMobs) is { } mv)
                            await SendAsync(ws, new { t = "entity", e = mv }, ct);
                        else if (BotEndpoints.NpcView(bot, cd, n) is { } nv)
                            await SendAsync(ws, new { t = "npc", e = nv }, ct);
                    }
                }
                foreach (var h in removed) await SendAsync(ws, new { t = "gone", handle = (int)h }, ct);

                // SELF is sampled, not evented — we move because our own script walked us, and there is no inbound packet for th…
                var sv = BotEndpoints.SelfView(bot, manager.ClientData);
                var svJson = JsonSerializer.Serialize(sv, Json);
                if (svJson != lastSelfJson)
                {
                    lastSelf = sv; lastSelfJson = svJson;
                    await SendAsync(ws, new { t = "self", self = lastSelf }, ct);
                }

                // Wait for the next change, but wake regularly enough to resample self / notice a handoff
                try { await wake.WaitAsync(SelfSampleMs, ct); } catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* client went away */ }
        catch (WebSocketException) { /* client went away mid-write */ }
        finally
        {
            bot.Events -= OnBotEvent;
            AttachZoneView(null);
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private static async Task SendAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

}
