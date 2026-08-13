using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Fiesta.Bot.Manager;

namespace Fiesta.Bot.Host;

/// <summary>
/// <c>GET /api/bots/{id}/stream</c> — a WebSocket stream of NDJSON: one parsed, friendly JSON document per
/// message, pushed the moment the wire says something changed.
///
/// <para><b>Why not <c>/events</c></b>, which is what this feature is called: that path is already a REST
/// endpoint returning typed event HISTORY with rollups. Registering a second endpoint on the same pattern
/// does not fail loudly — the existing one simply keeps winning, so the WebSocket silently never matched
/// and every upgrade came back as that endpoint's 404. Hence <c>/stream</c>.</para>
///
/// <para><b>Why this exists</b> (operator 2026-08-13): <i>"a websocket ndjson stream of all the packets we
/// receive but parsed/friendly, so we can implement a 'real time combat map' in /watch. Each entity is
/// updated when new info is received, instead of all in one go every second or whatever."</i> The watch
/// page polls <c>/entities</c> five times a second and re-reads EVERY nearby entity each time, so a mob
/// that just moved is indistinguishable from one that has not moved in a minute, and the map's resolution
/// is capped at the poll rate however fast the wire actually is.</para>
///
/// <para><b>Coalescing without a tick.</b> A dirty SET, not a queue: while a write is in flight, further
/// changes to the same entity collapse into one pending update instead of stacking. When the socket keeps
/// up that is per-packet immediacy; when it falls behind, updates merge rather than form a backlog that
/// arrives late and describes the past. No fixed flush interval is imposed — the socket's own speed is the
/// only pacing.</para>
///
/// <para><b>The read loop must never block.</b> ZoneView raises its notifications on the session read
/// loop, so the handlers here only mark a handle dirty and signal — all serialisation and I/O happens on
/// the pump task. That is the same contract <see cref="BotHandle.Events"/> documents.</para>
/// </summary>
public static class EventStreamEndpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How often SELF is re-checked and re-sent if it changed. Our own position moves because WE
    /// walk, which is a local decision with no inbound packet to hang an event on, so self is the one thing
    /// that genuinely needs sampling. It doubles as the socket keepalive. Entities stay purely
    /// event-driven — this interval does not pace them.</summary>
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

            // ⛔ ECHO A SUBPROTOCOL WHEN ONE WAS OFFERED. Per RFC 6455 a client that offers subprotocols
            // MUST fail the connection if the server selects none — and browsers enforce that, so a page
            // authenticating via `new WebSocket(url, ['bearer', token])` (see the auth middleware) would see
            // the handshake succeed server-side and then be torn down with no useful error. Select the
            // marker, never the token itself: the selected protocol is echoed in a response header.
            // Only ever the "bearer" MARKER, never whatever else was offered: the selected protocol is
            // echoed back in a response header, and the token travels in that same list.
            var offered = ctx.WebSockets.WebSocketRequestedProtocols;
            using var ws = offered.Contains("bearer")
                ? await ctx.WebSockets.AcceptWebSocketAsync("bearer")
                : await ctx.WebSockets.AcceptWebSocketAsync();
            await PumpAsync(ws, bot, manager!, ctx.RequestAborted);
        })
        .WithTags("Bots")
        .WithSummary("WebSocket NDJSON stream of parsed game events — per-entity updates as they arrive");
    }

    private static async Task PumpAsync(WebSocket ws, BotHandle bot, BotManager manager, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;

        // Dirty set + signal. A handle already pending collapses into the same update (see the class doc).
        var dirty = new HashSet<ushort>();
        var gone = new HashSet<ushort>();
        var wake = new SemaphoreSlim(0, 1);
        // Out-of-band one-shot messages (chat, hits, map changes) that are NOT per-entity state and must
        // not be collapsed — each one is a distinct thing that happened. Bounded and drop-oldest so a
        // stalled client can never grow this without limit or stall the read loop that writes to it.
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

        // ⛔ SUBSCRIBE TO THE *BOT*, NOT THE ZoneView OBJECT. ZoneView is swapped out on a cross-server
        // reconnect (map handoff), so a subscription captured once would go silent after the first gate the
        // bot walks through — the stream would look alive and carry nothing. Re-attach on every handoff.
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
                    // The single most useful thing on a combat map: WHO hit WHOM for how much, as it lands.
                    Post(new { t = "hit", attacker = (int)h.Attacker, defender = (int)h.Defender, dmg = (int)h.Damage, restHp = h.RestHp });
                    break;
                // ⛔ READ THE MAP FROM THE HANDOFF, NOT FROM bot.CurrentMap. This event is raised BY the
                // handoff; whether the bot's own field has been updated by the time a subscriber runs is
                // not something to rely on, and reading it stale is how the page kept the old map's name
                // (and therefore the old map's background art) after every transition.
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
                    break;
                }
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
            // HELLO = the complete current picture, so the client starts consistent and every later message
            // is a pure delta. Without it a fresh connection would only learn about entities that happen to
            // move next, and a stationary mob would never appear at all.
            // ⛔ IT IS THE *SAME SHAPE* /entities RETURNS, deliberately: the page already has one draw path
            // built on that object, so hello can be assigned straight into it and the deltas patch it in
            // place. A bespoke stream shape would mean a second renderer, and two renderers of the same
            // scene drift — a streamed mob would end up drawn differently from a polled one.
            await SendAsync(ws, new
            {
                t = "hello",
                id = bot.Id,
                mapDisplay = manager.ClientData?.MapDisplayName(bot.CurrentMap),
                entities = BotEndpoints.EntityPanel(bot, manager.ClientData),
                maxHp = bot.ZoneView?.MaxHp ?? 0,
            }, ct);

            object? lastSelf = null;
            var lastSelfJson = "";
            // Drain the client side so a close frame is noticed promptly; we never expect inbound data.
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

                // One-shot events first — they are the "what just happened" narration.
                while (oob.Reader.TryRead(out var msg)) await SendAsync(ws, msg, ct);

                // Then the coalesced entity state.
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
                    // Index once per drain rather than scanning NearbyNpcs per handle — a map-enter burst
                    // dirties every entity on the map at once, and that was quadratic.
                    var live = bot.ZoneView?.NearbyNpcs.ToDictionary(n => n.Handle) ?? [];
                    foreach (var h in changed)
                    {
                        if (!live.TryGetValue(h, out var n)) continue;
                        // MOB LAYER first; an entity that is not one is offered to the NPC layer.
                        // ⛔ THE NPC LAYER IS STREAMED TOO, because the map-enter burst is a genuine PACKET
                        // BURST and not something to poll for (operator 2026-08-13: "every map change sends
                        // a login burst with new char info, this packet can be streamed"). Every NPC and
                        // gate on the new map arrives as briefinfo the instant we spawn in — the same
                        // AddOrUpdateNpc that seeds the whole-map list — so the NPC layer rebuilds itself
                        // from the wire. The first version sent this list ONLY inside `hello`, which is why
                        // a map change left the page holding the previous map's NPCs and its background art.
                        if (BotEndpoints.MobView(bot, cd, n, aggro, questMobs) is { } mv)
                            await SendAsync(ws, new { t = "entity", e = mv }, ct);
                        else if (BotEndpoints.NpcView(bot, cd, n) is { } nv)
                            await SendAsync(ws, new { t = "npc", e = nv }, ct);
                    }
                }
                foreach (var h in removed) await SendAsync(ws, new { t = "gone", handle = (int)h }, ct);

                // SELF is sampled, not evented — we move because our own script walked us, and there is no
                // inbound packet for that. Only send when it actually changed, so a parked bot is silent.
                var sv = SelfView(bot);
                var svJson = JsonSerializer.Serialize(sv, Json);
                if (svJson != lastSelfJson)
                {
                    lastSelf = sv; lastSelfJson = svJson;
                    await SendAsync(ws, new { t = "self", self = lastSelf }, ct);
                }

                // Wait for the next change, but wake regularly enough to resample self / notice a handoff.
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

    /// <summary>Everything the map draws about US. The X/Y/Facing/Target names match the <c>Self</c> block
    /// inside <see cref="BotEndpoints.EntityPanel"/> so the client can patch it straight in; the rest is
    /// extra the stream can afford to carry because self is one small object.</summary>
    private static object SelfView(BotHandle bot)
    {
        var zv = bot.ZoneView;
        var p = bot.Position;
        return new
        {
            X = p is { } pp ? (double)pp.X : (double?)null,
            Y = p is { } pq ? (double)pq.Y : (double?)null,
            Facing = bot.FacingDeg >= 0 ? bot.FacingDeg : (double?)null,
            Target = (int)bot.CurrentTarget,
            // Our own speed, so the viewer can ease our marker between samples instead of stepping it.
            // We move by our own decision with no inbound packet, so self is SAMPLED (see SelfSampleMs) —
            // interpolation is what turns those samples back into motion.
            WalkSpeed = zv?.WalkSpeed ?? 0,
            Hp = zv?.Hp, MaxHp = zv?.MaxHp, Sp = zv?.Sp, MaxSp = zv?.MaxSp,
            InCombat = zv?.InCombat ?? false,
            Aggressors = zv?.Aggressors.Count ?? 0,
            Mounted = zv?.IsMounted ?? false,
            Dead = zv?.Dead ?? false,
        };
    }
}
