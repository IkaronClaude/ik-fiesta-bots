using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Metrics;
using Fiesta.Bot.Pathfinding;

namespace Fiesta.Bot.Host;

/// <summary>
/// HTTP control surface for the multi-bot manager: spawn / list / status / stop.
/// Thin mapping layer — the request DTO is translated to <see cref="BotSpawnOptions"/>
/// and all the work lives in <see cref="BotManager"/>. When no XOR table is
/// configured the manager can't connect, so every endpoint returns 503 with the
/// reason (the table is BYO — see PROJECT_PLAN.md).
/// </summary>
public static class BotEndpoints
{
    public static void MapBotEndpoints(this WebApplication app, BotManager? manager, string? unavailableReason)
    {
        var group = app.MapGroup("/api/bots").WithTags("Bots");

        // Guard: if the manager couldn't be built (no XOR table), fail every call
        // with a clear, actionable 503 rather than a null-ref.
        if (manager is null)
        {
            IResult Unavailable() => Results.Problem(
                title: "Bot manager unavailable",
                detail: unavailableReason ?? "The bot manager is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

            group.MapPost("/", Unavailable).WithSummary("Spawn a bot (unavailable)");
            group.MapGet("/", Unavailable).WithSummary("List bots (unavailable)");
            group.MapGet("/{id}", (string id) => Unavailable()).WithSummary("Bot status (unavailable)");
            group.MapPost("/{id}/stop", (string id) => Unavailable()).WithSummary("Stop a bot (unavailable)");
            group.MapPost("/{id}/say", (string id) => Unavailable()).WithSummary("Bot chat (unavailable)");
            group.MapPost("/{id}/cast", (string id) => Unavailable()).WithSummary("Bot cast (unavailable)");
            group.MapPost("/{id}/castground", (string id) => Unavailable()).WithSummary("Bot ground-cast (unavailable)");
            group.MapPost("/{id}/heal", (string id) => Unavailable()).WithSummary("Bot heal (unavailable)");
            group.MapPost("/{id}/attack", (string id) => Unavailable()).WithSummary("Bot attack (unavailable)");
            group.MapPost("/{id}/autoattack", (string id) => Unavailable()).WithSummary("Bot auto-attack (unavailable)");
            group.MapPost("/{id}/stopattack", (string id) => Unavailable()).WithSummary("Bot stop-attack (unavailable)");
            group.MapPost("/{id}/soulstone-sp", (string id) => Unavailable()).WithSummary("Bot soul-stone SP (unavailable)");
            group.MapPost("/{id}/soulstone-hp", (string id) => Unavailable()).WithSummary("Bot soul-stone HP (unavailable)");
            group.MapPost("/{id}/use-item", (string id) => Unavailable()).WithSummary("Bot use-item (unavailable)");
            group.MapPost("/{id}/shop-open", (string id) => Unavailable()).WithSummary("Bot open-shop (unavailable)");
            group.MapGet("/{id}/storage", (string id) => Unavailable()).WithSummary("Bot storage (unavailable)");
            group.MapPost("/{id}/quest/remote-accept", (string id) => Unavailable()).WithSummary("Bot remote-accept (unavailable)");
            group.MapPost("/{id}/storage/move", (string id) => Unavailable()).WithSummary("Bot storage move (unavailable)");
            group.MapGet("/{id}/shop", (string id) => Unavailable()).WithSummary("Bot shop list (unavailable)");
            group.MapPost("/{id}/buy", (string id) => Unavailable()).WithSummary("Bot buy (unavailable)");
            group.MapPost("/{id}/sell", (string id) => Unavailable()).WithSummary("Bot sell (unavailable)");
            group.MapPost("/{id}/enchant", (string id) => Unavailable()).WithSummary("Bot enchant (unavailable)");
            group.MapPost("/{id}/soulstone-hp-buy", (string id) => Unavailable()).WithSummary("Bot buy HP stone (unavailable)");
            group.MapPost("/{id}/soulstone-sp-buy", (string id) => Unavailable()).WithSummary("Bot buy SP stone (unavailable)");
            group.MapPost("/{id}/whisper", (string id) => Unavailable()).WithSummary("Bot whisper (unavailable)");
            group.MapGet("/{id}/inventory", (string id) => Unavailable()).WithSummary("Bot inventory (unavailable)");
            group.MapGet("/{id}/equipment", (string id) => Unavailable()).WithSummary("Bot equipment (unavailable)");
            group.MapGet("/{id}/npcs", (string id) => Unavailable()).WithSummary("Bot nearby NPCs (unavailable)");
            group.MapGet("/{id}/players", (string id) => Unavailable()).WithSummary("Bot nearby players (unavailable)");
            group.MapPost("/{id}/equip", (string id) => Unavailable()).WithSummary("Bot equip (unavailable)");
            group.MapPost("/{id}/pickup", (string id) => Unavailable()).WithSummary("Bot pickup (unavailable)");
            group.MapPost("/{id}/loot", (string id) => Unavailable()).WithSummary("Bot loot (unavailable)");
            group.MapGet("/{id}/drops", (string id) => Unavailable()).WithSummary("Bot ground drops (unavailable)");
            group.MapGet("/{id}/skills", (string id) => Unavailable()).WithSummary("Bot learned skills (unavailable)");
            group.MapPost("/{id}/walk", (string id) => Unavailable()).WithSummary("Bot walk (unavailable)");
            group.MapPost("/{id}/walkto", (string id) => Unavailable()).WithSummary("Bot walkto (unavailable)");
            group.MapPost("/{id}/gm", (string id) => Unavailable()).WithSummary("Bot GM command (unavailable)");
            group.MapPost("/{id}/townportal", (string id) => Unavailable()).WithSummary("Bot town-portal (unavailable)");
            group.MapPost("/{id}/use-gate", (string id) => Unavailable()).WithSummary("Bot use-gate (unavailable)");
            group.MapPost("/{id}/travelto", (string id) => Unavailable()).WithSummary("Bot travelto (unavailable)");
            group.MapPost("/{id}/stoptravel", (string id) => Unavailable()).WithSummary("Bot stoptravel (unavailable)");
            group.MapGet("/{id}/gates", (string id) => Unavailable()).WithSummary("Bot gates (unavailable)");
            group.MapGet("/{id}/route", (string id) => Unavailable()).WithSummary("Bot route plan (unavailable)");
            group.MapPost("/{id}/target", (string id) => Unavailable()).WithSummary("Bot target (unavailable)");
            group.MapPost("/{id}/untarget", (string id) => Unavailable()).WithSummary("Bot untarget (unavailable)");
            group.MapPost("/{id}/follow", (string id) => Unavailable()).WithSummary("Bot follow (unavailable)");
            group.MapPost("/{id}/unfollow", (string id) => Unavailable()).WithSummary("Bot unfollow (unavailable)");
            group.MapPost("/{id}/party/invite", (string id) => Unavailable()).WithSummary("Bot party invite (unavailable)");
            group.MapPost("/{id}/party/accept", (string id) => Unavailable()).WithSummary("Bot party accept (unavailable)");
            group.MapPost("/{id}/party/decline", (string id) => Unavailable()).WithSummary("Bot party decline (unavailable)");
            group.MapPost("/{id}/party/chat", (string id) => Unavailable()).WithSummary("Bot party chat (unavailable)");
            group.MapPost("/{id}/friend/add", (string id) => Unavailable()).WithSummary("Bot friend add (unavailable)");
            group.MapPost("/{id}/friend/confirm", (string id) => Unavailable()).WithSummary("Bot friend confirm (unavailable)");
            group.MapPost("/{id}/friend/delete", (string id) => Unavailable()).WithSummary("Bot friend delete (unavailable)");
            return;
        }

        group.MapPost("/", (SpawnBotRequest req) =>
        {
            BotSpawnOptions options;
            try { options = req.ToOptions(); }
            catch (ArgumentException ex) { return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] }); }

            try
            {
                var handle = manager.Spawn(options);
                return Results.Created($"/api/bots/{handle.Id}", handle.Snapshot());
            }
            catch (InvalidOperationException ex) // duplicate id
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .WithSummary("Spawn a bot")
        .WithDescription("Starts the login→WM→zone chain in the background. Returns immediately; poll GET /api/bots/{id} for progress.");

        group.MapGet("/", () => Results.Ok(manager.List().Select(b => b.Snapshot())))
            .WithSummary("List all bots with status");

        group.MapGet("/{id}", (string id) =>
        {
            var bot = manager.Get(id);
            return bot is null ? Results.NotFound() : Results.Ok(bot.Snapshot());
        })
        .WithSummary("Status of one bot (incl. recent log)");

        // ⚠️⚠️ `max` IS A REQUEST SIZE, NOT THE BUFFER SIZE. The ring buffer holds 100_000 lines
        // (BotHandle.MaxLogLines) — roughly 5 hours of verbose history at the measured ~6 lines/sec.
        // If you do not pass `max` you get the default and silently throw away almost all of it.
        // This cost real accuracy: with the old 200 default, a level=verbose read returned NINE SECONDS,
        // and "0 kills, 0 QUEST_MOB_KILL — the bot completes nothing" was reported off exactly that
        // sample while the bot was in a town phase and actually at q52 5/8 and climbing.
        //   GET /log?level=note&max=100000                     <- headlines, hours of history
        //   GET /log?level=info&from=13:41:15&to=13:42:00      <- drill into one moment
        // A NARROW SAMPLE IS NOT A NEGATIVE RESULT: "I did not see X" means nothing until you know the
        // window was wide enough to have contained X.
        // ── METRICS / BOT-WATCH (operator epic 2026-08-05: "a window into everything going on with the bot;
        // like a stat panel, you look at it and immediately know where the bot is") ────────────────────────
        // Lightweight companion to /metrics, for the watch page's COMBAT MAP. That map wants ~5 updates a
        // second and mob positions change constantly, but /metrics carries the whole metric snapshot, the
        // quest board and the trace counts — far too heavy to poll at that rate. This returns only what the
        // map draws.
        group.MapGet("/{id}/entities", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            return Results.Json(new
            {
                id = bot.Id,
                map = bot.CurrentMap,
                mapDisplay = manager.ClientData?.MapDisplayName(bot.CurrentMap),
                facing = bot.FacingDeg >= 0 ? bot.FacingDeg : (double?)null,
                maxHp = bot.ZoneView?.MaxHp ?? 0,
                entities = EntityPanel(bot, manager.ClientData),
            });
        })
        .WithSummary("Just the nearby mobs/party for the combat map — cheap enough to poll several times a second");

        group.MapGet("/{id}/metrics", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            return Results.Json(new
            {
                id = bot.Id,
                atUtc = DateTime.UtcNow,
                live = LivePanel(bot, manager.ClientData, manager.Knowledge),
                metrics = bot.Metrics.Snapshot(),
                maps = bot.Trace.MapCounts(recentOnly: true),
                tracePoints = bot.Trace.Count,
            });
        })
        .WithSummary("All metrics for a bot, plus the live hp/sp/exp panel")
        .WithDescription("Each metric reports 1m/5m/10m windows: count, avg, stdDev, min, max, p95, p99, sum, perMinute. " +
            "p95/p99 follow the metric's DIRECTION — for HigherIsBetter (hp, exp) they are the LOW tail ('95% of the " +
            "time at least X'); for LowerIsBetter (damageTaken, deaths) the HIGH tail. Samples are batched (default " +
            "500ms) so the window means TIME, not caller frequency.");

        // 📡 STREAMING metrics as NDJSON (operator: "Bonus points if you make this endpoint streamable, e.g.
        // post 'UpdateRate: 10s' and then every 10s the server pushes a new json object"). One JSON object
        // per line, flushed each tick, so `curl -N` and browser fetch-readers both work with no framing
        // beyond a newline. Ends when the client disconnects (the cancellation token) or maxSeconds elapses —
        // an un-bounded stream would otherwise pin a thread per forgotten tab.
        group.MapGet("/{id}/metrics/stream", async (string id, double? updateRate, int? maxSeconds,
            HttpContext ctx, CancellationToken ct) =>
        {
            var bot = manager.Get(id);
            if (bot is null) { ctx.Response.StatusCode = 404; return; }
            var every = TimeSpan.FromSeconds(Math.Clamp(updateRate ?? 10, 0.5, 300));
            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(maxSeconds ?? 3600, 1, 86_400));
            ctx.Response.ContentType = "application/x-ndjson";
            ctx.Response.Headers.CacheControl = "no-cache";
            // ⚠️ MUST use the Web defaults (camelCase). Results.Json — which GET /metrics uses — applies them
            // automatically, so a bare `new JsonSerializerOptions()` here emits PascalCase and the SAME
            // payload arrives with different key casing depending on which endpoint you call. Caught live:
            // the stream's `live.Map/Hp` read as null against a parser written for `/metrics`'s `live.map/hp`,
            // which looks exactly like "the bot has no data" rather than "the keys are spelled differently".
            var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            };
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var payload = new
                {
                    atUtc = DateTime.UtcNow,
                    live = LivePanel(bot, manager.ClientData, manager.Knowledge),
                    metrics = bot.Metrics.Snapshot(),
                    maps = bot.Trace.MapCounts(recentOnly: true),
                    tracePoints = bot.Trace.Count,
                };
                await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(payload, opts) + "\n", ct);
                await ctx.Response.Body.FlushAsync(ct);   // flush per line or the client sees nothing until buffer fill
                try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
            }
        })
        .WithSummary("Stream metrics as NDJSON, one JSON object per line")
        .WithDescription("?updateRate=SECONDS (default 10, clamped 0.5-300) and ?maxSeconds=N (default 3600) to bound " +
            "the stream. Try: curl -N '.../metrics/stream?updateRate=5'. Each line is the same shape as GET /metrics.");

        // Position trace for the browser-rendered heatmap. Stores raw timestamp+map+coord and lets the client
        // poll with `since` (operator: "so I can watch what the bot is doing live and also it takes up less
        // data") — the server never rasterises anything.
        group.MapGet("/{id}/trace", (string id, long? since, string? map, bool? recent) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var pts = bot.Trace.Since(since ?? 0, map, recent ?? false);
            return Results.Json(new
            {
                nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                count = pts.Count,
                recentWindowMinutes = PositionTrace.RecentWindow.TotalMinutes,
                points = pts.Select(p => new { t = p.T, map = p.Map, x = p.X, y = p.Y }),
            });
        })
        .WithSummary("Position trace points (timestamp+map+coord) for the live heatmap")
        .WithDescription("Poll with ?since=<last t you saw> to get only new points. ?map=RouN filters to one map; " +
            "?recent=true drops samples older than 30 minutes. One sample per second.");

        group.MapGet("/{id}/log", (string id, string? level, int? max, string? from, string? to) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var maxLevel = (level?.ToLowerInvariant()) switch
            {
                "note" or "n" => BotLogLevel.Note,
                "info" or "i" => BotLogLevel.Info,
                _ => BotLogLevel.Verbose,   // default: the full firehose
            };
            var lines = bot.RecentLines(max ?? 2000, maxLevel, from, to);
            return Results.Text(string.Join("\n", lines) + "\n", "text/plain");
        })
        .WithSummary("Tail a bot's log as plain text, filtered by verbosity")
        .WithDescription("Query: level=note|info|verbose (default verbose=everything), max=N lines (default 2000), " +
            "from=HH:mm[:ss[.fff]] and to=... to restrict to a UTC time window. Drill-down workflow: read level=note, " +
            "spot a headline time, then re-read level=info&from=..&to=.. around it. (max is a REQUEST size, not the " +
            "buffer size — the buffer holds 100k lines.) A window spanning midnight is not supported and returns nothing. " +
            "note=headline only (quest accept/finish, level-up, death, purchase, errors); info adds kills/quest-progress; " +
            "verbose adds move/cast/auto-attack. Plain text so `curl .../log?level=info` is directly readable.");

        // 🥈 SILVER RULE ENDPOINT (operator 2026-08-05): "I'd have to run a probe script, which stops the
        // leveller" is an ARCHITECTURE FAILURE — add the endpoint instead. This is that endpoint for quest
        // targeting, the exact data whose absence caused a wrong diagnosis twice in one session: I could not
        // read bot.quest(id).objectives without replacing the running driver, so I inferred a quest's target
        // from its NAME and was wrong. Everything here is read-only and disturbs nothing.
        //
        // It answers "WHY is the bot pursuing this quest, and is that target actually killable?" by joining
        // the quest's objectives to MobInfo (level / maxHp / gradeType) and to the persisted deprioritize
        // mark. Concretely it exposes the bug class found on 2026-08-05: "Rare Material 4"(q2511) is a
        // type-2 COLLECT objective for an item dropped by mob22 "Marlone" (L26, 9916 HP, GradeType 1) —
        // a boss, reachable only through a collect objective, which the type-1-only boss screen missed.
        group.MapGet("/{id}/quests", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var view = bot.ZoneView;
            var cd = manager.ClientData;
            if (view is null || cd is null) return Results.Ok(Array.Empty<object>());

            var rows = new List<object>();
            foreach (var (qid, status) in view.ActiveQuests)
            {
                var q = cd.Quest(qid);
                var objectives = new List<object>();
                var maxGrade = 0;
                if (q is not null)
                {
                    foreach (var o in q.Objectives)
                    {
                        // Resolve the mob the SAME way the driver must: an objective with mob 0 falls back
                        // to the quest's own kill target. Reported for BOTH kill (1) and collect (2) types —
                        // reporting only type 1 is precisely how the boss went unnoticed.
                        var mob = o.Mob != 0 ? o.Mob : q.ObjectiveMob;
                        var mi = mob > 0 ? cd.Mob(mob) : null;
                        // A boss must be something you FIGHT, so it needs HP. GradeType alone is not enough:
                        // gathering nodes score high too (mob5018 "Herb" is L150, 0 HP, GradeType 5), and
                        // treating those as bosses would shelve harmless collection quests.
                        if (mi is not null && mi.MaxHp > 0 && mi.GradeType > maxGrade) maxGrade = mi.GradeType;
                        objectives.Add(new
                        {
                            type = o.Type,
                            typeName = o.Type switch { 1 => "kill", 2 => "collect", _ => $"type{o.Type}" },
                            mob,
                            viaObjectiveMob = o.Mob == 0 && mob > 0,
                            item = o.Item,
                            need = o.Count,
                            mobName = mi?.Name ?? "",
                            mobLevel = mi?.Level ?? -1,
                            mobMaxHp = mi?.MaxHp ?? -1,
                            mobGrade = mi?.GradeType ?? -1,
                        });
                    }
                }
                rows.Add(new
                {
                    id = qid,
                    name = cd.QuestName(qid),
                    status,
                    repeatable = q?.Repeatable ?? false,
                    exp = q?.ExpReward ?? 0,
                    // Remote accept / hand-in flags (QuestData +25 / +88). The driver does NOT use these yet:
                    // START_REQ 0x4414 + doQuest(npc=0) were tried live and did not accept, so the real
                    // remote-accept sequence is still undecoded (P1). Exposed so "which quests even claim to
                    // support it" is answerable from live data instead of by reading offsets.
                    remoteAccept = q?.RemoteAcceptable ?? false,      // @25 bIsWaitListProgress = REMOTE ACCEPT
                    questListVisible = q?.IsWaitListView ?? false,    // @24 = visible in quest list, NOT accept
                    remoteHandIn = q?.IsInstantHandIn ?? false,
                    startNpc = q?.StartNpc ?? 0,
                    turnInNpc = q?.TurnInNpc ?? 0,
                    objectiveMob = q?.ObjectiveMob ?? -1,
                    objectives,
                    // The verdict fields — what a targeting decision should hinge on.
                    targetsBoss = maxGrade >= 1,
                    deprioritizedAtLevel = manager.Knowledge.QuestDeprioritizedAtLevel(bot.KnowledgeScope, qid),
                });
            }
            return Results.Ok(new { level = bot.Level, count = rows.Count, quests = rows });
        })
        .WithSummary("Every ACTIVE quest with its objectives joined to MobInfo + the persisted deprioritize mark")
        .WithDescription("Read-only; safe on a running bot (does NOT replace the driver like applying a probe script does). " +
            "Per objective: type (kill/collect), the resolved mob (incl. the mob-0 -> objectiveMob fallback), and that mob's " +
            "level/maxHp/gradeType. `targetsBoss` is true when any objective's mob is GradeType>=1 — the check that must " +
            "cover COLLECT objectives too, not just kills.");

        group.MapPost("/{id}/stop", async (string id) =>
        {
            var stopped = await manager.StopAsync(id);
            return stopped ? Results.Ok(new { id, stopped = true }) : Results.NotFound();
        })
        .WithSummary("Stop a bot and remove it from the manager");

        group.MapPost("/{id}/packetlog", (string id, PacketLogRequest? req) =>
        {
            var enabled = req?.Enabled ?? true;
            var (found, on, path) = manager.SetPacketLog(id, enabled);
            return found
                ? Results.Ok(new { id, enabled = on, path })
                : Results.NotFound();
        })
        .WithSummary("Toggle a tailable both-directions plaintext packet dump (hex+ASCII) for a bot")
        .WithDescription("Body {\"enabled\":true|false} (default true). Returns the log file path to `tail -f`. Captures every S→C and C→S frame interleaved, XOR-decoded, with opcode + name + hex/ASCII. Survives zone handoffs.");

        group.MapPost("/{id}/say", async (string id, SayRequest req) =>
        {
            if (string.IsNullOrEmpty(req.Text))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["text is required"] });
            return ToResult(await manager.SayAsync(id, req.Text), id, new { id, said = req.Text });
        })
        .WithSummary("Make a bot say a line in its zone (local chat)");

        group.MapPost("/{id}/cast", async (string id, CastRequest req) =>
        {
            if (req.Skill is not { } skill)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["skill"] = ["skill id is required"] });
            var target = req.Target ?? 0;
            return ToResult(await manager.CastAsync(id, skill, target), id, new { id, cast = skill, target });
        })
        .WithSummary("Cast a skill on a target handle (replays client target+mode+cast sequence)");

        group.MapPost("/{id}/castground", async (string id, CastGroundRequest req) =>
        {
            if (req.Skill is not { } skill || req.X is not { } x || req.Y is not { } y)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["req"] = ["skill, x, y are required"] });
            return ToResult(await manager.CastGroundAsync(id, skill, x, y), id, new { id, castGround = skill, x, y });
        })
        .WithSummary("Cast a location-targeted (ground/AoE) skill at a coordinate, e.g. Frost Nova (no target unit)");

        group.MapPost("/{id}/heal", async (string id, CastRequest req) =>
        {
            if (req.Skill is not { } skill)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["skill"] = ["heal skill id is required"] });
            var r = await manager.HealSelfAsync(id, skill);
            return r == BotManager.ActionResult.NotInZone && manager.Get(id)?.SelfHandle is null
                ? Results.Conflict(new { error = "self handle unknown (not fully in zone yet)" })
                : ToResult(r, id, new { id, healed = "self", skill });
        })
        .WithSummary("Cast a heal skill on yourself (self-targeted)");

        group.MapPost("/{id}/attack", async (string id, AttackRequest req) =>
        {
            if (req.Skill is not { } skill)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["skill"] = ["skill id is required"] });
            var r = await manager.AttackAsync(id, skill, req.Target ?? 0);
            return r == BotManager.ActionResult.NotFound && manager.Get(id) is not null
                ? Results.Conflict(new { error = "no target given and no mob in view" })
                : ToResult(r, id, new { id, attack = skill, target = req.Target });
        })
        .WithSummary("Attack: cast a damage skill on a target handle, or the nearest mob in view");

        group.MapPost("/{id}/autoattack", async (string id, AttackRequest req) =>
        {
            var r = await manager.AutoAttackAsync(id, req.Target ?? 0);
            return r == BotManager.ActionResult.NotFound && manager.Get(id) is not null
                ? Results.Conflict(new { error = "no target given and no mob in view" })
                : ToResult(r, id, new { id, autoAttack = req.Target ?? 0 });
        })
        .WithSummary("Begin melee auto-attack (BASHSTART) on a target handle, or the nearest mob in view");

        group.MapPost("/{id}/stopattack", async (string id) =>
            ToResult(await manager.StopAttackAsync(id), id, new { id, stoppedAttack = true }))
        .WithSummary("Stop melee auto-attack (BASHSTOP)");

        group.MapPost("/{id}/soulstone-sp", async (string id) =>
            ToResult(await manager.UseSoulStoneSpAsync(id), id, new { id, soulStoneSp = true }))
        .WithSummary("Recharge SP from the soul-stone reserve (in-game 'use an SP stone', 0x5009)");

        group.MapPost("/{id}/soulstone-hp", async (string id) =>
            ToResult(await manager.UseSoulStoneHpAsync(id), id, new { id, soulStoneHp = true }))
        .WithSummary("Recharge HP from the soul-stone reserve (in-game 'use an HP stone', 0x5007)");

        group.MapPost("/{id}/use-item", async (string id, UseItemRequest req) =>
        {
            if (req.Slot is not { } slot)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slot"] = ["inventory slot is required"] });
            // invenType 9 = the normal item bag (from the client capture); the
            // earlier default of 0 made the server reply "no item at that address".
            return ToResult(await manager.UseItemAsync(id, slot, req.InvenType ?? 9), id, new { id, usedSlot = slot });
        })
        .WithSummary("Use an inventory item by slot");

        group.MapPost("/{id}/shop-open", async (string id, ShopOpenRequest req) =>
        {
            if (req.NpcHandle is not { } h)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["npcHandle"] = ["npcHandle is required"] });
            return ToResult(await manager.OpenShopAsync(id, h, req.MenuOption ?? 1), id, new { id, openedShop = h });
        })
        .WithSummary("Open a merchant's shop (click + menu-ack) so the server sends its sell list — then GET /shop");

        // ── PERSONAL STORAGE (warehouse) ──────────────────────────────────────────────────────────────
        // Read + act, so a deposit can be EXERCISED and VERIFIED directly instead of only through the
        // driver's policy (Silver Rule: build the path, don't guess whether it works).
        group.MapGet("/{id}/storage", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var v = bot.ZoneView;
            if (v is null) return Results.Ok(new { open = false, box = -1, items = Array.Empty<object>() });
            return Results.Ok(new
            {
                open = v.StorageOpen,
                box = v.StorageBox,
                cen = v.StorageCen,
                page = v.StoragePage,
                maxPage = v.StorageMaxPage,
                cellChanges = v.CellChangeCount,
                items = v.StorageItems.Select(it => new
                {
                    slot = it.Slot,
                    id = (int)it.ItemId,
                    name = manager.ClientData?.Item(it.ItemId)?.Name ?? "",
                }),
            });
        })
        .WithSummary("Personal storage: contents, container box, money, page, and whether a session is open")
        .WithDescription("Open it first by clicking the storage keeper via POST /shop-open (a storage open reports " +
            "shopKind=storage). `box` is the container id — 6, wire-verified from Z:/Storage.pcapng.");

        group.MapPost("/{id}/storage/move", async (string id, StorageMoveRequest req) =>
        {
            if (req.FromSlot is not { } f)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fromSlot"] = ["fromSlot is required"] });
            if (req.ToSlot is not { } t)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["toSlot"] = ["toSlot is required"] });
            var deposit = req.Deposit ?? true;
            var r = await manager.StorageMoveAsync(id, f, t, deposit);
            return ToResult(r, id, new { id, deposit, fromSlot = f, toSlot = t });
        })
        .WithSummary("Move one item between the bag and storage (NC_ITEM_RELOC_REQ), CONFIRMED by CELLCHANGE")
        .WithDescription("deposit=true moves bag->storage, false moves storage->bag (the same packet both ways). " +
            "Requires an OPEN storage session; refuses otherwise. A missing CELLCHANGE within 3s is reported as a " +
            "FAILURE (CRUTCH[CRIT] in the bot log), never assumed to be success.");

        group.MapGet("/{id}/shop", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var view = bot.ZoneView;
            if (view is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            var cd = manager.ClientData;
            return Results.Ok(new { id, npc = view.ShopNpc, count = view.ShopItems.Count,
                items = view.ShopItems.Select(it => new { itemId = it, name = cd?.ItemName(it) }) });
        })
        .WithSummary("List what the currently-open shop sells (itemId + name from client ItemInfo)");

        group.MapPost("/{id}/buy", async (string id, BuyRequest req) =>
        {
            if (req.ItemId is not { } itemId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["itemId"] = ["itemId is required"] });
            return ToResult(await manager.BuyAsync(id, itemId, req.Lot ?? 1), id, new { id, bought = itemId, lot = req.Lot ?? 1 });
        })
        .WithSummary("Buy an item by id from the open shop (NC_ITEM_BUY_REQ; needs money — cheat with /gm getmoney)");

        group.MapPost("/{id}/sell", async (string id, SellRequest req) =>
        {
            if (req.Slot is not { } slot)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slot"] = ["bag slot is required"] });
            return ToResult(await manager.SellAsync(id, slot, req.Lot ?? 1), id, new { id, soldSlot = slot, lot = req.Lot ?? 1 });
        })
        .WithSummary("Sell a bag item by slot to the open shop (NC_ITEM_SELL_REQ)");

        group.MapPost("/{id}/enchant", async (string id, EnchantRequest req) =>
        {
            if (req.Equip is not { } equip || req.Raw is not { } raw)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["req"] = ["equip (slot) and raw (stone slot) are required"] });
            return ToResult(await manager.EnchantAsync(id, equip, raw,
                req.RawLeft ?? 0xFF, req.RawMiddle ?? 0xFF, req.RawRight ?? 0xFF, req.Money ?? 0),
                id, new { id, enchant = equip, raw });
        })
        .WithSummary("Enchant gear (NC_ITEM_UPGRADE_REQ): equip slot + stone inventory slots (raw=primary Elrue/Lixir/Xir; left/middle/right=safety/bonus, 0xFF=none)");

        group.MapPost("/{id}/soulstone-hp-buy", async (string id, StoneBuyRequest req) =>
            ToResult(await manager.BuyHpStoneAsync(id, req.Number ?? 1), id, new { id, boughtHpStones = req.Number ?? 1 }))
        .WithSummary("Buy HP soul-stone charges into the reserve (NC_SOULSTONE_HP_BUY_REQ; needs money)");

        group.MapPost("/{id}/soulstone-sp-buy", async (string id, StoneBuyRequest req) =>
            ToResult(await manager.BuySpStoneAsync(id, req.Number ?? 1), id, new { id, boughtSpStones = req.Number ?? 1 }))
        .WithSummary("Buy SP soul-stone charges into the reserve (NC_SOULSTONE_SP_BUY_REQ; needs money)");

        group.MapPost("/{id}/whisper", async (string id, WhisperRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.To) || string.IsNullOrEmpty(req.Text))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to/text"] = ["to and text are required"] });
            return ToResult(await manager.WhisperAsync(id, req.To, req.Text), id, new { id, to = req.To, whispered = req.Text });
        })
        .WithSummary("Whisper a message to a named player");

        group.MapGet("/{id}/inventory", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var view = bot.ZoneView;
            var inv = view?.Inventory;
            if (view is null || inv is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            var cd = manager.ClientData;
            // NAME + STACK COUNT + the sell/keep inputs, not a bare id list. Why (2026-08-06): the bot
            // deadlocked on "bag FULL + nothing sellable", blocking the hand-in of THREE complete quests,
            // which emptied the quest board and dropped it into the last-resort grind at 0 exp/2min. The
            // bag was 48/48 with FIVE separate slots of one potion and NINE of return scrolls — and the
            // endpoint could not say whether those were full stacks or mergeable partials, because it
            // returned only ids. The count was decoded all along (ZoneView.ItemCount); nothing surfaced it.
            // sellPrice/gradeType/demandLv are exactly the fields the driver's classifier keys on, so a
            // "why is this not sellable?" question is answerable here instead of by reading ItemInfo by hand.
            var rows = inv.OrderBy(kv => kv.Key).Select(kv =>
            {
                var info = cd?.Item(kv.Value);
                return new
                {
                    slot = kv.Key,
                    itemId = kv.Value,
                    count = view.ItemCount(kv.Key),
                    name = info?.Name ?? "",
                    type = info?.Type ?? -1,
                    itemClass = info?.ItemClass ?? -1,
                    sellPrice = info?.SellPrice ?? -1,
                    gradeType = info?.GradeType ?? -1,
                    demandLv = info?.DemandLv ?? -1,
                    maxLot = info?.MaxLot ?? -1,
                };
            }).ToList();
            return Results.Ok(new { id, used = rows.Count, capacity = Session.ZoneView.BagPageSlots * Session.ZoneView.BagPagesAssumed, items = rows });
        })
        .WithSummary("Bag contents with names, stack counts and the sell/keep fields");

        group.MapGet("/{id}/equipment", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var eq = bot.ZoneView?.Equipment;
            if (eq is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            return Results.Ok(new { id, worn = eq.OrderBy(kv => kv.Key).Select(kv => new { equipSlot = kv.Key, itemId = kv.Value }) });
        })
        .WithSummary("List the bot's worn gear (equip slot → itemId)");

        group.MapGet("/{id}/npcs", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var npcs = bot.ZoneView?.NearbyNpcs;
            if (npcs is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            // Resolve each numeric mobId to its client-side name/level (MobInfo) so the
            // list is human-readable (e.g. "Teleport Gate") — null if no client data.
            var cd = manager.ClientData;
            return Results.Ok(new { id, count = npcs.Count, npcs = npcs
                .OrderBy(n => n.MobId)
                .Select(n => {
                    var m = cd?.Mob(n.MobId);
                    return new { handle = n.Handle, mobId = n.MobId, name = m?.Name, level = m?.Level,
                        isNpc = m?.IsNpc, playerSide = m?.IsPlayerSide, type = m?.Type,
                        huntable = cd?.IsHuntableEnemy(n.MobId), mode = n.Mode, x = n.X, y = n.Y,
                        isGate = n.IsGate, linkMap = n.LinkMap };
                }) });
        })
        .WithSummary("List NPCs/mobs the bot can see (handle, mobId, name, level, coord, gate→destMap) from zone broadcasts + client MobInfo");

        group.MapGet("/{id}/players", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var players = bot.ZoneView?.NearbyPlayers;
            if (players is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            return Results.Ok(new { id, count = players.Count, players = players
                .OrderBy(p => p.Name)
                .Select(p => new { handle = p.Handle, name = p.Name, cls = p.Class, level = p.Level, x = p.X, y = p.Y }) });
        })
        .WithSummary("List players the bot can see (handle, name, class, level, coord) from zone broadcasts");

        group.MapPost("/{id}/equip", async (string id, EquipRequest req) =>
        {
            if (req.Slot is not { } slot)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slot"] = ["inventory slot is required"] });
            return ToResult(await manager.EquipAsync(id, slot), id, new { id, equippedFromSlot = slot });
        })
        .WithSummary("Equip the inventory item at the given slot");

        group.MapGet("/{id}/drops", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var drops = bot.ZoneView?.Drops;
            if (drops is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            var cd = manager.ClientData;
            return Results.Ok(new { id, count = drops.Count, drops = drops
                .Select(d => new { handle = d.Handle, itemId = d.ItemId, name = cd?.ItemName(d.ItemId),
                    x = d.X, y = d.Y, dropMob = d.DropMobHandle }) });
        })
        .WithSummary("List items on the ground in view (handle, itemId, name, coord, dropMob) from DROPEDITEM broadcasts");

        group.MapGet("/{id}/skills", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var skills = bot.ZoneView?.LearnedSkills;
            if (skills is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            var cd = manager.ClientData;
            return Results.Ok(new { id, count = skills.Count, skills = skills
                .OrderBy(s => s)
                .Select(s => new { skillId = s, name = cd?.SkillName(s) }) });
        })
        .WithSummary("List the character's learned skills (skillId + name) from the zone-login skill list");

        group.MapPost("/{id}/pickup", async (string id, PickupRequest req) =>
        {
            if (req.Handle is not { } handle)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["handle"] = ["ground-item handle is required"] });
            return ToResult(await manager.PickupAsync(id, handle), id, new { id, pickedHandle = handle });
        })
        .WithSummary("Pick up a ground item by handle (must already be close — NC_ITEM_PICK_REQ)");

        group.MapPost("/{id}/loot", async (string id, LootRequest req) =>
            ToResult(await manager.LootAsync(id, req.Handle ?? 0), id, new { id, looted = req.Handle ?? 0 }))
        .WithSummary("Walk to a ground drop and pick it up (nearest if no handle given)");

        group.MapPost("/{id}/inventory-sort", async (string id) =>
            ToResult(await manager.SortInventoryAsync(id), id, new { id, sorted = true }))
        .WithSummary("Fire the client's inventory auto-sort (compact+stack the bag — NC_ITEM_AUTO_ARRANGE_INVEN_REQ 0x304A)");

        group.MapPost("/{id}/click-npc", async (string id, PickupRequest req) =>
        {
            if (req.Handle is not { } h)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["handle"] = ["npc handle is required"] });
            return ToResult(await manager.ClickNpcAsync(id, h), id, new { id, clickedNpc = h });
        })
        .WithSummary("Click an NPC (starts its quest dialogue / menu)");

        group.MapGet("/{id}/quest", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var q = bot.ZoneView?.PendingQuest;
            return Results.Ok(new { id, pending = q is null ? null : new { questId = q.QuestId, qsc = q.Qsc, dialogId = q.DialogId } });
        })
        .WithSummary("The pending quest-dialogue step the server is prompting (null if none)");

        group.MapPost("/{id}/quest/do", async (string id, PickupRequest req) =>
        {
            if (req.Handle is not { } h)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["handle"] = ["npc handle is required"] });
            return ToResult(await manager.DriveQuestDialogueAsync(id, h), id, new { id, npc = h });
        })
        .WithSummary("Drive a full quest dialogue with an NPC (click + ACK every page; accept or turn-in)");

        // Trigger a REMOTE ACCEPT on demand. Exists so the path can be PROVEN without applying a probe
        // script (which would replace the running leveler) — the operator's Silver Rule: if verifying
        // something needs a probe, the missing thing is an endpoint. The driver calls the same
        // RemoteAcceptQuestAsync, so a success here is a success there.
        group.MapPost("/{id}/quest/remote-accept", async (string id, RemoteAcceptRequest req) =>
        {
            if (req.QuestId is not { } q)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["questId"] = ["questId is required"] });
            var r = await manager.RemoteAcceptQuestAsync(id, q);
            var bot = manager.Get(id);
            return ToResult(r, id, new
            {
                id,
                questId = q,
                // The verdict, from the bot's own state — not from the fact that we sent a packet.
                active = bot?.ZoneView?.IsQuestActive(q) ?? false,
                remoteAcceptable = manager.ClientData?.Quest(q)?.RemoteAcceptable ?? false,
            });
        })
        .WithSummary("Accept a quest REMOTELY from the quest log (no travel, no NPC click) — verified by the quest going active")
        .WithDescription("Refuses quests not flagged @25 bIsWaitListProgress. Sends NC_QUEST_START_REQ then drains the " +
            "served script pages, exactly as captured in Z:/QuestsRemoteAndMulti.pcapng. `active` in the response is read " +
            "back from the bot's own quest state, so a false there means it genuinely did not take.");

        // OPERATOR OVERRIDE: give a deprioritized quest another chance, without waiting for a level-up.
        // The mark's only automatic expiry is LEVEL-UP, and every mark is written at the level we fled at —
        // so once a few quests are marked at the CURRENT level the whole board reads deprioritized and the
        // bot drops to the last-resort grind, which is the slowest possible route to the level that would
        // clear them. That ratchet is live right now (seven quests marked at 26 with the char at 26).
        // A human can see from the watch page that a mark is stale; this is the button that says so.
        // ⚠️ Clears BOTH the mark and the per-level death counter — clearing only the mark leaves the
        // counter at the threshold, so the next death re-marks instantly and the override reads as a no-op.
        // The LIFETIME death total is deliberately kept (it ranks a historically deadly quest lower; the
        // override re-opens the decision, it does not erase the history).
        group.MapPost("/{id}/quest/{questId:int}/undeprioritize", (string id, int questId) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var knowledge = manager.Knowledge;
            if (knowledge is null) return Results.Problem("knowledge store unavailable");
            var was = knowledge.QuestDeprioritizedAtLevel(bot.KnowledgeScope, questId);
            var cleared = knowledge.ClearQuestDeprioritized(bot.KnowledgeScope, questId);
            var deaths = knowledge.ClearQuestDeathsAtLevel(bot.KnowledgeScope, questId, (int)bot.Level);
            var name = manager.ClientData?.QuestName(questId) ?? $"q{questId}";
            // Log it on the BOT's own tail. An operator override that only shows in an HTTP response is
            // invisible in the log everyone actually reads to explain what the bot did next.
            bot.LogOperatorAction($"[operator] UN-DEPRIORITIZED {name}(q{questId}) — mark was lvl{was}" +
                    (deaths > 0 ? $", cleared {deaths} death(s) recorded at lvl{bot.Level}" : "") +
                    " — the driver will re-evaluate it on its next quest pass.");
            return Results.Ok(new
            {
                id, questId, name,
                clearedMark = cleared,
                wasDeprioritizedAtLevel = was,
                clearedDeathsAtLevel = deaths,
                level = (int)bot.Level,
            });
        })
        .WithSummary("Clear a quest's flee-deprioritization (operator override)")
        .WithDescription("Clears the persisted mark AND the per-level death counter that would immediately " +
            "re-apply it. The lifetime death total is kept. `clearedMark:false` means there was no mark to " +
            "clear — which is not an error, just nothing to do.");

        group.MapGet("/{id}/quest-dialog/{dialogId:int}", (string id, int dialogId) =>
        {
            var cd = manager.ClientData;
            return Results.Ok(new { dialogId, text = cd?.QuestDialog(dialogId) ?? "" });
        })
        .WithSummary("Resolve a quest dialog/title id to its text (QuestDialog.shn)");

        group.MapGet("/{id}/quest-info/{questId:int}", (string id, int questId) =>
        {
            var cd = manager.ClientData;
            var q = cd?.Quest(questId);
            if (q is null) return Results.NotFound();
            return Results.Ok(new
            {
                q.Id, q.StartNpc, q.TurnInNpc, q.MinLevel, q.MaxLevel, q.IsNeedLevel, q.Class, q.LinkedQuest,
                q.ObjectiveMob, q.PrereqQuest,
                q.NeedsNpc, q.NeedsItem, q.NeedsItemId, q.NeedsClass, q.IsWaitListView,
                remoteAcceptable = q.RemoteAcceptable, remoteProgress = q.IsWaitListProgress, q.IsInstantHandIn, q.Region, q.QuestType, q.Repeatable,
                title = cd!.QuestDialog(q.Title),
                npcs = q.Npcs, objectives = q.Objectives, rewards = q.Rewards,
                q.StartScript, q.ActionScript, q.FinishScript
            });
        })
        .WithSummary("Decoded QuestData.shn for a quest id (StartNPC, objectives, rewards, scripts)");

        group.MapPost("/{id}/quest/answer", async (string id, QuestAnswerRequest? req) =>
            ToResult(await manager.ProceedQuestAsync(id, req?.Result ?? 1), id, new { id, answered = req?.Result ?? 1 }))
        .WithSummary("Answer the pending quest-dialogue step (result=1 proceeds/accepts)");

        group.MapPost("/{id}/quest/reward", async (string id, QuestRewardRequest req) =>
        {
            if (req.QuestId is not { } qid || req.Index is not { } idx)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["reward"] = ["questId and index are required"] });
            return ToResult(await manager.SelectQuestRewardAsync(id, qid, idx), id, new { id, quest = qid, rewardIndex = idx });
        })
        .WithSummary("Select a quest reward item by index (e.g. the class-appropriate reward)");

        group.MapPost("/{id}/walkto", (string id, WalkToRequest req) =>
        {
            if (req.ToX is not { } tx || req.ToY is not { } ty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["req"] = ["toX, toY are required"] });
            var bot = manager.Get(id);
            // map defaults to the bot's current map (tracked across transitions); from
            // defaults to the bot's tracked position (seeded from the zone-login spawn
            // coord, advanced as it walks) — so callers can pass just toX/toY.
            var map = !string.IsNullOrWhiteSpace(req.Map) ? req.Map! : bot?.CurrentMap;
            if (string.IsNullOrWhiteSpace(map))
                return Results.Conflict(new { error = "no map given and bot's current map is unknown (not in zone yet)" });
            uint fx, fy;
            if (req.FromX is { } rfx && req.FromY is { } rfy) (fx, fy) = (rfx, rfy);
            else if (bot?.Position is { } pos) (fx, fy) = (pos.X, pos.Y);
            else return Results.Conflict(new { error = "no from coord given and bot position unknown (not in zone yet)" });
            var grid = LoadGrid(map);
            if (grid is null)
                return Results.Problem(title: "Block grid unavailable",
                    detail: $"Set BLOCKINFO_DIR and ensure {map}.shbd exists.", statusCode: StatusCodes.Status503ServiceUnavailable);
            var path = PathFinder.FindPath(grid, fx, fy, tx, ty);
            if (path.Count == 0) return Results.Conflict(new { error = "no path to target (start/goal blocked or unreachable)" });
            var wp = PathFinder.Simplify(path);
            return ToResult(manager.WalkPath(id, wp), id, new { id, map, waypoints = wp.Count, tiles = path.Count });
        })
        .WithSummary("Pathfind across a map's block grid and walk there (map + coords; map/from default to the bot's current map/position)");

        group.MapPost("/{id}/walk", async (string id, WalkRequest req) =>
        {
            if (req.ToX is not { } tx || req.ToY is not { } ty || req.FromX is not { } fx || req.FromY is not { } fy)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["coords"] = ["fromX, fromY, toX, toY are required"] });
            return ToResult(await manager.WalkAsync(id, fx, fy, tx, ty), id, new { id, from = new[] { fx, fy }, to = new[] { tx, ty } });
        })
        .WithSummary("Walk from (fromX,fromY) to (toX,toY) — one MoverunCmd step");

        group.MapPost("/{id}/townportal", async (string id, TownPortalRequest req) =>
        {
            if (req.NpcHandle is not { } h || req.Dest is not { } d)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["req"] = ["npcHandle and dest are required"] });
            return ToResult(await manager.TownPortalAsync(id, h, d), id, new { id, npcHandle = h, dest = d });
        })
        .WithSummary("Use a town multi-select portal (target+click portal NPC, select destination index)");

        group.MapPost("/{id}/use-gate", async (string id, UseGateRequest req) =>
        {
            if (req.GateHandle is not { } h)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["gateHandle"] = ["gateHandle is required"] });
            return ToResult(await manager.UseGateAsync(id, h, req.DestMap), id, new { id, gate = h, dest = req.DestMap });
        })
        .WithSummary("Take a field gate by NPC handle (target+click; optional destMap for multi-dest gates)");

        group.MapPost("/{id}/travelto", (string id, TravelToRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.To))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["to"] = ["destination map is required"] });
            var (result, route) = manager.TravelTo(id, req.To!, req.UnitsPerSec ?? 120.0);
            return result switch
            {
                BotManager.TravelResult.Started => Results.Accepted($"/api/bots/{id}", new
                {
                    id, to = req.To, hops = route!.Count,
                    route = route.Select(e => new { e.FromMap, e.ToMap })
                }),
                BotManager.TravelResult.AlreadyThere => Results.Ok(new { id, to = req.To, alreadyThere = true }),
                BotManager.TravelResult.NoRoute => Results.NotFound(new { error = $"no known gate route to '{req.To}' from here — explore via /gates first", to = req.To }),
                BotManager.TravelResult.NotInZone => Results.Conflict(new { error = "bot is not in zone yet (or current map unknown)" }),
                _ => Results.NotFound(),
            };
        })
        .WithSummary("Autonomously travel to a map: BFS the learned gate graph, then walk-to-gate + take-gate per hop (background)");

        group.MapPost("/{id}/stoptravel", (string id) =>
            ToResult(manager.StopTravel(id), id, new { id, travelling = false }))
        .WithSummary("Stop an in-progress travelto (halts the bot where it is)");

        group.MapGet("/{id}/gates", (string id) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            if (bot.CurrentMap is null || bot.ZoneView is null)
                return Results.Conflict(new { error = "bot is not in zone yet" });
            var observed = manager.ObserveGates(id); // fold the bot's view into the shared graph
            var gates = bot.ZoneView.NearbyNpcs.Where(n => n.IsGate)
                .Select(n => new { handle = n.Handle, x = n.X, y = n.Y, linkMap = n.LinkMap });
            return Results.Ok(new { id, map = bot.CurrentMap, observed, gates });
        })
        .WithSummary("List gates in view (and fold them into the shared world map graph)");

        group.MapGet("/{id}/route", (string id, string to) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            if (bot.CurrentMap is not { } from)
                return Results.Conflict(new { error = "bot's current map is unknown" });
            manager.ObserveGates(id); // make sure in-view gates are in the graph first
            var route = manager.Graph.Route(from, to);
            if (route is null) return Results.NotFound(new { error = $"no known route {from} -> {to}", from, to });
            return Results.Ok(new { id, from, to, hops = route.Count,
                route = route.Select(e => new { e.FromMap, e.ToMap, gate = new { e.GateHandle, e.GateX, e.GateY } }) });
        })
        .WithSummary("Plan a gate route from the bot's current map to ?to=<map> over the learned graph (read-only)");

        group.MapPost("/{id}/gm", async (string id, GmRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["command"] = ["command is required"] });
            // GM commands are chat-routed; the server keys off the '&'/'$' prefix.
            // Prepend '&' if the caller omitted a prefix, for convenience.
            var cmd = req.Command.Trim();
            if (cmd is [not ('&' or '$'), ..]) cmd = "&" + cmd;
            return ToResult(await manager.GmAsync(id, cmd), id, new { id, gm = cmd });
        })
        .WithSummary("Issue a GM command (e.g. levelup 46, makeitem SafeProtection01, learnskill 1580, getmoney 1000000)");

        // ── Targeting / follow (zone) ──────────────────────────────────────────
        group.MapPost("/{id}/target", async (string id, TargetRequest req) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            ushort target;
            if (req.Target is { } t) target = t;
            else if (!string.IsNullOrWhiteSpace(req.Name))
            {
                var p = bot.ZoneView?.NearbyPlayers
                    .FirstOrDefault(p => string.Equals(p.Name, req.Name, StringComparison.OrdinalIgnoreCase));
                if (p is null) return Results.Conflict(new { error = $"no nearby player named '{req.Name}'" });
                target = p.Handle;
            }
            else return Results.ValidationProblem(new Dictionary<string, string[]> { ["target/name"] = ["target handle or name is required"] });
            return ToResult(await manager.TargetAsync(id, target), id, new { id, target });
        })
        .WithSummary("Target a player by handle or name (party-tab targeting)");

        group.MapPost("/{id}/untarget", async (string id) =>
            ToResult(await manager.UntargetAsync(id), id, new { id, untargeted = true }))
        .WithSummary("Clear the current target (Esc)");

        group.MapPost("/{id}/follow", (string id, FollowRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["name is required"] });
            var r = manager.Follow(id, req.Name!, req.FollowDist ?? 60.0, req.UnitsPerSec ?? 120.0);
            return r == BotManager.ActionResult.NotFound
                ? Results.Conflict(new { error = $"bot or nearby player '{req.Name}' not found" })
                : ToResult(r, id, new { id, following = req.Name });
        })
        .WithSummary("Follow a nearby player by name (target + chase; client-side, drops at map change)");

        group.MapPost("/{id}/unfollow", (string id) =>
            ToResult(manager.StopFollow(id), id, new { id, following = false }))
        .WithSummary("Stop following");

        // ── Party (WorldManager link) ──────────────────────────────────────────
        group.MapPost("/{id}/party/invite", async (string id, NameRequest req) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["name is required"] })
                : ToResult(await manager.PartyInviteAsync(id, req.Name!), id, new { id, invited = req.Name }))
        .WithSummary("Invite a player to your party");

        group.MapPost("/{id}/party/accept", async (string id, NameRequest? req) =>
            ToResult(await manager.PartyAcceptAsync(id, req?.Name), id, new { id, accepted = req?.Name ?? "(pending invite)" }))
        .WithSummary("Accept a party invite (named inviter, or the tracked pending one if omitted)");

        group.MapPost("/{id}/party/decline", async (string id, NameRequest? req) =>
            ToResult(await manager.PartyDeclineAsync(id, req?.Name), id, new { id, declined = req?.Name ?? "(pending invite)" }))
        .WithSummary("Decline a party invite (named inviter, or the tracked pending one if omitted)");

        group.MapPost("/{id}/party/chat", async (string id, SayRequest req) =>
            string.IsNullOrEmpty(req.Text)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["text is required"] })
                : ToResult(await manager.PartyChatAsync(id, req.Text!), id, new { id, partyChat = req.Text }))
        .WithSummary("Send a line to party chat");

        // ── Friend list (WorldManager link) ────────────────────────────────────
        group.MapPost("/{id}/friend/add", async (string id, NameRequest req) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["name is required"] })
                : ToResult(await manager.FriendAddAsync(id, req.Name!), id, new { id, friendRequest = req.Name }))
        .WithSummary("Send a friend request to a player");

        group.MapPost("/{id}/friend/confirm", async (string id, FriendConfirmRequest req) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["requester name is required"] })
                : ToResult(await manager.FriendConfirmAsync(id, req.Name!, req.Accept), id, new { id, requester = req.Name, accepted = req.Accept }))
        .WithSummary("Answer an incoming friend request (accept=true adds, false declines)");

        group.MapPost("/{id}/friend/delete", async (string id, NameRequest req) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["name is required"] })
                : ToResult(await manager.FriendDeleteAsync(id, req.Name!), id, new { id, removed = req.Name }))
        .WithSummary("Remove a player from your friend list");
    }

    // Block grids loaded from BLOCKINFO_DIR/<Map>.shbd (BYO), cached per map.
    private static readonly ConcurrentDictionary<string, BlockGrid?> _grids = new(StringComparer.OrdinalIgnoreCase);

    internal static BlockGrid? LoadGrid(string map) => _grids.GetOrAdd(map, m =>
    {
        var dir = Environment.GetEnvironmentVariable("BLOCKINFO_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var path = Path.Combine(dir, m + ".shbd");
        try
        {
            if (!File.Exists(path)) return null;
            var grid = BlockGrid.Load(path);
            // EROSION for scenario-instance maps (2026-07-15). The bot-vs-client compare + MOVEFAIL-desync logging
            // proved the finale failure is a nav collision mismatch: the instance .shbd walkable border is ~1 tile
            // WIDER than the SERVER collision, so our path clips cells the server MOVEFAILs → the bot can never
            // hold a stable position inside a trigger box → e.g. Zone_Mob04's LightOff (fires on the ack re-
            // checking server-pos) never dispatches. The real client runs the SAME map with 0 MOVEFAIL. Erode the
            // walkable area 1 tile to match the server so the path never clips → clean run. Data-driven: only maps
            // WITH a .aid (= scenario instances) are eroded; field maps keep the operator's relaxed nav untouched.
            // BuildEroded keeps the instance FULLY connected (entry→Kebings→skeletons→Door4→Chiefs).
            // REVERTED 2026-07-15: 1-tile erosion made instance nav WORSE (R2 MOVEFAIL 33→88, bot pinned 10min,
            // can't reach the skeleton wave) — it over-constrains the corridors so combat-approach clips even more.
            // The MOVEFAILs are NOT a simple edge-inset (the Zone_Mob04 439u Y-gap is over-navigation into a
            // server-blocked region past the trigger, not a 1-tile border). Erosion is the wrong lever. Left OFF.
            // if (File.Exists(Path.Combine(dir, m + ".aid"))) grid.EnableErosion();
            // DYNAMIC SCENARIO-DOOR COLLISION (2026-07-15): attach the .sbi door overlays so the pathfinder
            // matches the SERVER's door-aware collision (the .shbd is baked all-doors-open; a closed door is a
            // wall only the overlay knows). This is the root fix for the JCQ instance-nav MOVEFAIL storm —
            // replaces the erosion experiment (wrong lever). Door STATES are pushed live from ZoneView.
            try { grid.AttachDoors(Fiesta.Bot.Pathfinding.DoorCollision.Load(Path.Combine(dir, m + ".sbi"))); }
            catch { /* no .sbi / malformed → grid runs .shbd-only, unchanged */ }
            // COMPANION .bdt (2026-07-21): attach the reverse-engineered 50-unit quadtree collision for the
            // measuring-stick diagnostic (compare .shbd vs .bdt at live MOVEFAIL points). Read-only; does not
            // change pathfinding yet. Null/absent on flat maps → grid runs .shbd-only, unchanged.
            try { grid.AttachBdt(Fiesta.Bot.Pathfinding.BdtGrid.Load(Path.Combine(dir, m + ".bdt"))); }
            catch { /* no .bdt / malformed → no companion, unchanged */ }
            return grid;
        }
        catch { return null; }
    });

    // Instance doors loaded from BLOCKINFO_DIR/<Map>.sbi (BYO), cached per map. Empty list for non-instance maps.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Fiesta.Bot.Navigation.InstanceDoor>> _doors = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<Fiesta.Bot.Navigation.InstanceDoor> LoadDoors(string map) => _doors.GetOrAdd(map, m =>
    {
        var dir = Environment.GetEnvironmentVariable("BLOCKINFO_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return Array.Empty<Fiesta.Bot.Navigation.InstanceDoor>();
        try { return Fiesta.Bot.Navigation.InstanceDoors.Load(Path.Combine(dir, m + ".sbi")); }
        catch { return Array.Empty<Fiesta.Bot.Navigation.InstanceDoor>(); }
    });

    // Scenario trigger areas from BLOCKINFO_DIR/<Map>.aid (BYO), cached per map. Empty for non-scenario maps.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Fiesta.Bot.Navigation.ScenarioArea>> _areas = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<Fiesta.Bot.Navigation.ScenarioArea> LoadAreas(string map) => _areas.GetOrAdd(map, m =>
    {
        var dir = Environment.GetEnvironmentVariable("BLOCKINFO_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return Array.Empty<Fiesta.Bot.Navigation.ScenarioArea>();
        try { return Fiesta.Bot.Navigation.ScenarioAreas.Load(Path.Combine(dir, m + ".aid")); }
        catch { return Array.Empty<Fiesta.Bot.Navigation.ScenarioArea>(); }
    });

    private static IResult ToResult(BotManager.ActionResult result, string id, object ok) => result switch
    {
        BotManager.ActionResult.Sent => Results.Ok(ok),
        BotManager.ActionResult.NotInZone => Results.Conflict(new { error = "bot is not in zone yet" }),
        _ => Results.NotFound(),
    };

    /// <summary>The always-on vitals for the watch panel: what a human glances at first.
    /// <para>Deliberately built ON TOP of the existing <see cref="BotHandle.Snapshot"/> rather than
    /// re-deriving fields from ZoneView. Re-deriving would create a second, silently-diverging view of the
    /// same truth — the panel would eventually disagree with /api/bots/{id} and there would be no way to tell
    /// which was right. Only the genuinely NEW numbers are added here.</para></summary>
    private static object LivePanel(BotHandle bot) => LivePanel(bot, null, null);

    /// <summary>Overload that can also report SKILL COOLDOWNS. The cooldown LENGTH lives in client data
    /// (ActiveSkill.DelayTime) and the last-use TIMESTAMP lives in ZoneView, so neither alone can answer
    /// "is this skill ready?" — the manager is where both are reachable, hence the optional parameter.</summary>
    private static object LivePanel(BotHandle bot, GameData.ClientData? cd, Manager.NpcKnowledge? knowledge = null)
    {
        var snap = bot.Snapshot();
        var zv = bot.ZoneView;
        var bagUsed = 0;
        if (zv is not null) foreach (var kv in zv.Inventory) if (kv.Value > 0) bagUsed++;
        return new
        {
            snap.Phase, snap.Map, snap.Position, snap.Level, snap.Exp,
            // The map's DISPLAY name from MapInfo.shn's `Name` column ("Elderine Cemetery"), beside the
            // internal code in snap.Map ("EldCem01"). Null when unknown — the page falls back to the code
            // rather than showing an invented name.
            MapDisplay = cd?.MapDisplayName(snap.Map),
            snap.Hp, snap.MaxHp, snap.Sp, snap.MaxSp,
            snap.HpStones, snap.SpStones, snap.InCombat, snap.Aggressors,
            snap.NearestAggressorDist, snap.Mounted, snap.Dead, snap.Drops, snap.Script,
            Money = zv is { Money: >= 0 } ? zv.Money : (long?)null,
            BagUsed = bagUsed,
            BagFree = bot.ZoneView?.BagFreeSlots,
            BagCapacity = bot.ZoneView?.BagCapacity,
            // ⚠️ BagFree/BagCapacity are INFERRED (48, +24 if any slot >= 48 is occupied). BagFull is a
            // STALE EVENT FLAG — set when a pickup FAILED with 0x346 — so `false` only means "no pickup has
            // failed", NOT "there is room": a STACKABLE item merges into an existing stack and picks up fine
            // at 48/48. The two are answering DIFFERENT questions, so a mismatch between them is normal and
            // is NOT evidence about capacity (I previously mistook it for exactly that). Show both, label
            // the inferred pair as inferred, and let a human read them as what they are.
            // BagFull is a STALE EVENT FLAG (set when a pickup FAILED with 0x346), not a capacity statement:
            // `false` only means no pickup has failed, and a STACKABLE item picks up fine at full occupancy.
            BagFullServerSignal = bot.ZoneView?.BagFull,
            Skills = SkillPanel(bot, cd),
            // Character sheet: the fixed stats a human reads next to HP. Decoded at zone-entry since
            // 2026-07-29 but only ever logged — now stored and surfaced. Null when the CHAR_PARAMETER_DATA
            // block never arrived (a "burst" login): NOT KNOWN, which is not the same as zero.
            Stats = zv?.Stats is { } st ? new
            {
                st.Str, st.End, st.Dex, st.Int, st.Spr,
                st.DmgMin, st.DmgMax, st.Def, st.Aim, st.Evasion, st.MagicDmg, st.MagicDef,
                // The exp bar the real client draws. Band is per-LEVEL, so ExpIntoLevel/ExpBand is the
                // denominator for "N EXP (x%)". Null-ish (0) only when the parameter block never arrived.
                st.PrevExp, st.NextExp,
                ExpBand = st.NextExp > st.PrevExp ? st.NextExp - st.PrevExp : 0,
            } : null,
            FreeStatPoints = zv is { FreeStatPoints: >= 0 } ? zv.FreeStatPoints : (int?)null,
            Passives = zv?.LearnedPassives?.Select(pid => new { Id = (int)pid, Name = cd?.PassiveSkillName(pid) ?? "" }).ToArray(),
            Facing = bot.FacingDeg >= 0 ? bot.FacingDeg : (double?)null,
            // Live quest board: what is accepted, how far along, and what each one wants. Progress is the
            // SERVER's credited count (NC_QUEST_NOTIFY_MOB_KILL), not our own kill tally — a mob dying
            // credits nothing if the quest is not actually tracking it.
            Quests = QuestPanel(bot, cd, knowledge),
            // What the DRIVER says it is doing right now — which quest it picked, what phase it is in, and
            // where it is walking and why. Published by the Lua (bot.setFocus); the host does not re-derive
            // it, because its own quest ordering only MIRRORS the driver's sort and can disagree with what
            // the driver actually chose. Null until the driver has published (no script, or not yet decided).
            Focus = bot.Focus is { } f ? new
            {
                f.QuestId,
                QuestName = f.QuestId > 0 ? (cd?.QuestName(f.QuestId) ?? $"q{f.QuestId}") : null,
                f.Phase,
                f.Destination,
                f.Reason,
                // Staleness, so the page can show an intent going cold instead of presenting a frozen one
                // as current — a driver that stopped publishing looks identical to one that is still going.
                AgeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - f.AtUnixMs,
            } : null,
            // Entities for the zoomed combat map. Positions are RAW game coords; the page centres on self.
            Entities = EntityPanel(bot, cd),
            // The survivability inequality, surfaced where a human can see both sides at once.
            SustainableHealDps = zv is { SustainableHealDps: > 0 } ? zv.SustainableHealDps : (double?)null,
            IncomingDps5s = zv?.IncomingDamageSince(TimeSpan.FromSeconds(5)),
        };
    }

    /// <summary>The accepted-quest board with live state. <c>Need</c> is summed over the quest's kill and
    /// collect objectives from client data; <c>Progress</c> is the server-credited count. A quest whose
    /// definition is missing from client data is still listed (with its id) rather than dropped — a silent
    /// omission would hide exactly the QuestData decode gaps we care about.</summary>
    private static object[] QuestPanel(BotHandle bot, GameData.ClientData? cd, Manager.NpcKnowledge? knowledge)
    {
        var zv = bot.ZoneView;
        if (zv is null) return [];
        var outp = new List<object>();
        foreach (var (qid, status) in zv.ActiveQuests)
        {
            var qd = cd?.Quest(qid);
            var need = 0; var mobs = new List<string>(); var goals = new List<object>(); var onMap = false;
            if (qd is not null)
                for (var oi = 0; oi < qd.Objectives.Count; oi++)
                {
                    var o = qd.Objectives[oi];
                    need += o.Count;
                    var mobName = o.Mob > 0 ? (cd?.Mob(o.Mob)?.Name ?? $"mob{o.Mob}") : null;
                    if (mobName is not null) mobs.Add(mobName);
                    // Is this objective's mob on the map we are standing on? MobCoordinate is client data,
                    // the same table the driver's LOCAL-first preference uses.
                    var here = o.Mob > 0 && cd?.MobCoordinatesAll(o.Mob)?.Any(ml =>
                        string.Equals(ml.Map, bot.CurrentMap, StringComparison.OrdinalIgnoreCase)) == true;
                    if (here) onMap = true;
                    goals.Add(new
                    {
                        Index = oi,
                        Kind = o.Type == 1 ? "kill" : o.Type == 2 ? "collect" : $"type{o.Type}",
                        // Resolve the ITEM name too — a collect goal used to read "collect item3083", which
                        // is an internal token, not a thing a person recognises. Same rule as mob names:
                        // show the name, and keep the id visible so UI and logs cross-reference.
                        Target = mobName ?? (o.Item > 0 ? (cd?.ItemName(o.Item) is { Length: > 0 } inm ? inm : $"item{o.Item}") : "?"),
                        MobId = o.Mob,
                        ItemId = o.Item,
                        Need = o.Count,
                        // Per-objective credit, from the objIdx the server sends with each kill credit.
                        Progress = zv.QuestObjProgress(qid, oi),
                        OnCurrentMap = here,
                    });
                }
            var prog = zv.QuestProgress(qid);
            // WHY a quest is not being worked. Only reasons the HOST can derive from data it actually has —
            // the driver's own verdicts (PASSIVE/shelved, UNSOLVABLE) live in Lua and are not duplicated
            // here, because a second half-copy of that logic would drift and lie. Absent reason = "nothing
            // known against it", NOT "definitely fine".
            // Short LABEL for the column, full story in `detail` (the page shows it on hover). The long
            // form was blowing the panel width out on its own.
            string? reason = null, detail = null;
            var deprioAt = knowledge?.QuestDeprioritizedAtLevel(bot.KnowledgeScope, qid) ?? -1;
            if (deprioAt >= 0 && deprioAt >= (int)bot.Level)
            {
                reason = "deaths";
                detail = $"deprioritized at lvl{deprioAt} because it killed us — frees at lvl{deprioAt + 1}";
            }
            else if (qd is null) { reason = "no data"; detail = "no QuestData entry for this quest (decode gap)"; }
            else if (need == 0 && qd.Objectives.Count == 0) { reason = "no goals"; detail = "active, but no objectives resolved from client data"; }
            else
            {
                foreach (var o in qd.Objectives)
                {
                    if (o.Mob <= 0) continue;
                    var md = cd?.Mob(o.Mob);
                    if (md is null) continue;
                    // TOWER OF IYZEL: instance-only mobs a solo bot can never reach. Detect by MOB ID RANGE —
                    // hardcoding these is explicitly authorised (CLAUDE.md) precisely because classifying by
                    // quest NAME is banned. Without it the Iyzel quests sorted to the TOP on their fat exp and
                    // the panel advertised permanently-shelved work as "next up".
                    if ((o.Mob >= 8100 && o.Mob <= 8138) || o.Mob == 9186 || o.Mob == 9187)
                    { reason = "instance"; detail = $"Tower of Iyzel — instance-only ({md.Name}); needs a party"; break; }
                    // ⛔ ONLY judge danger on something that can actually FIGHT. Gathering nodes and prop NPCs
                    // live in MobInfo too and carry nonsense combat columns — live 2026-08-06 the panel
                    // announced "Herb is a boss/elite (L150, 0 hp)" on a PLANT-COLLECTION quest, because
                    // GradeType alone said elite. Zero MaxHp or an NPC flag means it is not a fight, so
                    // neither the boss nor the over-level test means anything for it.
                    if (md.IsNpc || md.MaxHp <= 0) continue;
                    if (md.GradeType >= 1)
                    { reason = "boss"; detail = $"{md.Name} is a boss/elite — L{md.Level}, {md.MaxHp} hp"; break; }
                    if (md.Level > (int)bot.Level + 3)
                    { reason = "over-level"; detail = $"{md.Name} is L{md.Level} vs our {bot.Level}"; break; }
                }
            }
            outp.Add(new
            {
                Id = qid,
                Name = cd?.QuestName(qid) ?? $"q{qid}",
                Reason = reason,
                ReasonDetail = detail,
                Status = (int)status,
                Progress = prog,
                Need = need,
                Ready = need > 0 && prog >= need,
                Targets = mobs,
                Goals = goals,
                OnCurrentMap = onMap,
                ExpReward = qd?.ExpReward ?? 0,
                Repeatable = qd?.Repeatable ?? false,
                Known = qd is not null,
            });
        }
        // Ordered to MIRROR the driver's documented sort — "LOCAL > closer-to-DONE > exp", with anything
        // ready to hand in first and anything the driver has a reason against last.
        // ⚠️ This MIRRORS that rule; it is not the driver's own ranking. The real decision (bands,
        // deprioritization, solvability) lives in the Lua, and a second copy of it here would drift and
        // start lying. Treat this as "roughly what it will pick next", not as the bot's actual queue.
        return outp
            .OrderByDescending(o => ((dynamic)o).Ready)
            .ThenBy(o => ((dynamic)o).Reason is null ? 0 : 1)
            .ThenByDescending(o => ((dynamic)o).OnCurrentMap)
            .ThenByDescending(o => { var d = (dynamic)o; return d.Need > 0 ? (double)d.Progress / d.Need : 0.0; })
            .ThenByDescending(o => ((dynamic)o).ExpReward)
            .ToArray();
    }

    /// <summary>Everything currently in AoI that the combat map draws: mobs (with facing, cur/max hp and
    /// whether they are huntable) and party members. Handles are included so the page can correlate a row
    /// with the death report and the packet ring.
    /// ⚠️ <c>Hp</c> is null until an entity has actually been hit — absent means "never seen hurt", NOT
    /// full and NOT zero. <c>Dir</c> is the raw SHINE_COORD_TYPE byte (0-255); its scale is not pinned, so
    /// it must not be compared numerically with our own <c>Facing</c> in degrees.</summary>
    private static object EntityPanel(BotHandle bot, GameData.ClientData? cd)
    {
        var zv = bot.ZoneView;
        var self = bot.Position;
        var mobs = new List<object>();
        var party = new List<object>();
        if (zv is not null)
        {
            var aggro = new HashSet<ushort>(zv.Aggressors);
            // Mobs any ACTIVE quest wants — kill objectives and collect objectives alike (a collect drops
            // from a mob, and checking only kills is the exact miss that let an Iyzel collect quest through).
            var questMobs = new HashSet<int>();
            if (cd is not null)
                foreach (var qid in zv.ActiveQuests.Keys)
                    if (cd.Quest(qid) is { } qd)
                        foreach (var o in qd.Objectives)
                            if (o.Mob > 0) questMobs.Add(o.Mob);
            foreach (var n in zv.NearbyNpcs)
            {
                if (n.IsGate) continue;
                var md = cd?.Mob(n.MobId);
                mobs.Add(new
                {
                    Handle = (int)n.Handle,
                    MobId = (int)n.MobId,
                    Name = md?.Name ?? $"mob{n.MobId}",
                    Level = md?.Level ?? 0,
                    X = (double)n.X, Y = (double)n.Y,
                    Dir = (int)n.Dir,
                    Hp = zv.EntityHp(n.Handle) is { } h ? (double?)h : null,
                    MaxHp = md?.MaxHp ?? 0,
                    Huntable = zv.IsHuntableMob?.Invoke(n.MobId) ?? true,
                    Aggro = aggro.Contains(n.Handle),
                    QuestMob = questMobs.Contains(n.MobId),
                    // Danger is LEARNED, not baked: the hardest hit this mob type has actually landed on us
                    // (-1 = never hit us, i.e. unknown rather than safe). The page compares it to our MaxHp.
                    MaxHitSeen = zv.MobHitMax(n.MobId),
                    Dist = self is { } p ? Math.Sqrt(Math.Pow((double)n.X - p.X, 2) + Math.Pow((double)n.Y - p.Y, 2)) : (double?)null,
                });
            }
            foreach (var m in bot.PartyMembers.Values)
                party.Add(new { m.Name, Level = (int)m.Level, Hp = (double)m.Hp, MaxHp = (double)m.MaxHp, X = (double)m.X, Y = (double)m.Y });
        }
        return new { Self = self is { } sp ? new { X = (double)sp.X, Y = (double)sp.Y } : null, Mobs = mobs, Party = party };
    }

    /// <summary>Learned skills with their cooldown state: length from client data, last-use from ZoneView,
    /// remaining computed here. Returns an empty list rather than null when client data is unavailable, so
    /// the panel renders "no skills" instead of breaking — and NEVER invents a cooldown for a skill whose
    /// DelayTime we cannot read (unknown stays unknown, it does not become "ready").</summary>
    private static object[] SkillPanel(BotHandle bot, GameData.ClientData? cd)
    {
        var zv = bot.ZoneView;
        if (zv is null || cd is null) return [];
        var now = DateTime.UtcNow;
        var outp = new List<object>();
        foreach (var id in zv.LearnedSkills)
        {
            var si = cd.Skill(id);
            if (si is null) continue;                     // not in client data — cannot judge, so omit
            var lastAt = zv.SkillLastCastAtUtc(id);
            double? remaining = null;
            if (si.DelayTimeMs > 0 && lastAt is { } la)
                remaining = Math.Max(0, si.DelayTimeMs - (now - la).TotalMilliseconds);
            outp.Add(new
            {
                Id = (int)id,
                Name = cd.SkillName(id) ?? "",
                CooldownMs = si.DelayTimeMs,
                Misc = si.IsMisc,   // gathering/mount/event toy — the page filters these out by default
                SpCost = si.Sp,
                LastCastAtUtc = lastAt,
                RemainingMs = remaining,
                Ready = remaining is null or <= 0,        // never cast this session => ready
            });
        }
        return outp.OrderBy(o => ((dynamic)o).Name as string ?? "").ToArray();
    }

}

/// <summary>Body for <c>POST /api/bots/{id}/say</c>.</summary>
public sealed record SayRequest
{
    public string? Text { get; init; }
}

/// <summary>Body for /packetlog. <c>Enabled</c> true (default) starts the dump, false stops it.</summary>
public sealed record PacketLogRequest
{
    public bool? Enabled { get; init; }
}

/// <summary>Body for the party/friend name-only endpoints (invite / accept / decline /
/// friend add / delete). <c>Name</c> is the target or inviter/requester char name.</summary>
public sealed record NameRequest
{
    public string? Name { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/target</c> — give either a zone
/// <c>Target</c> handle or a player <c>Name</c> to resolve from the bot's view.</summary>
public sealed record TargetRequest
{
    public ushort? Target { get; init; }
    public string? Name { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/follow</c>.</summary>
public sealed record FollowRequest
{
    public string? Name { get; init; }
    public double? FollowDist { get; init; }
    public double? UnitsPerSec { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/friend/confirm</c>.</summary>
public sealed record FriendConfirmRequest
{
    public string? Name { get; init; }
    public bool Accept { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/cast</c> and <c>/heal</c>.</summary>
public sealed record CastRequest
{
    public ushort? Skill { get; init; }
    public ushort? Target { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/castground</c> — a location-targeted skill
/// (e.g. Frost Nova) cast at world coordinate (<c>X</c>,<c>Y</c>), no target unit.</summary>
public sealed record CastGroundRequest
{
    public ushort? Skill { get; init; }
    public uint? X { get; init; }
    public uint? Y { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/attack</c>. Omit <c>Target</c> to hit the
/// nearest mob in view.</summary>
public sealed record AttackRequest
{
    public ushort? Skill { get; init; }
    public ushort? Target { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/use-item</c>.</summary>
public sealed record UseItemRequest
{
    public byte? Slot { get; init; }
    public byte? InvenType { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/equip</c>.</summary>
public sealed record EquipRequest
{
    public byte? Slot { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/pickup</c> — the ground-item handle
/// (from <c>GET /drops</c>). The bot must already be standing near it.</summary>
public sealed record PickupRequest
{
    public ushort? Handle { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/loot</c>. Omit <c>Handle</c> to loot the
/// nearest ground drop (walk to it + pick).</summary>
public sealed record LootRequest
{
    public ushort? Handle { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/quest/answer</c>. <c>Result</c> defaults to 1
/// (proceed/accept).</summary>
public sealed record QuestAnswerRequest
{
    public uint? Result { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/quest/reward</c>.</summary>
public sealed record QuestRewardRequest
{
    public ushort? QuestId { get; init; }
    public uint? Index { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/shop-open</c> — the merchant NPC handle.
/// <c>MenuOption</c> picks the NPC-menu entry (default 1 = shop).</summary>
public sealed record ShopOpenRequest
{
    public ushort? NpcHandle { get; init; }
    public byte? MenuOption { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/storage/move</c>. <c>Deposit</c> defaults to true
/// (bag → storage); false withdraws (storage → bag) using the same RELOC packet.</summary>
/// <summary>Body for <c>POST /api/bots/{id}/quest/remote-accept</c>.</summary>
public sealed record RemoteAcceptRequest
{
    public ushort? QuestId { get; init; }
}

public sealed record StorageMoveRequest
{
    public byte? FromSlot { get; init; }
    public byte? ToSlot { get; init; }
    public bool? Deposit { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/buy</c>. <c>Lot</c> defaults to 1.</summary>
public sealed record BuyRequest
{
    public ushort? ItemId { get; init; }
    public uint? Lot { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/sell</c>. <c>Lot</c> defaults to 1.</summary>
public sealed record SellRequest
{
    public byte? Slot { get; init; }
    public uint? Lot { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/enchant</c>. <c>Equip</c> = gear's equip slot,
/// <c>Raw</c> = primary enhance-stone inventory slot; <c>RawLeft/Middle/Right</c> = optional
/// safety/bonus stones (omit for 0xFF = none).</summary>
public sealed record EnchantRequest
{
    public byte? Equip { get; init; }
    public byte? Raw { get; init; }
    public byte? RawLeft { get; init; }
    public byte? RawMiddle { get; init; }
    public byte? RawRight { get; init; }
    public uint? Money { get; init; }
}

/// <summary>Body for the soul-stone buy endpoints. <c>Number</c> of charges (default 1).</summary>
public sealed record StoneBuyRequest
{
    public ushort? Number { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/whisper</c>.</summary>
public sealed record WhisperRequest
{
    public string? To { get; init; }
    public string? Text { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/walk</c>. Map coords (u32).</summary>
public sealed record WalkRequest
{
    public uint? FromX { get; init; }
    public uint? FromY { get; init; }
    public uint? ToX { get; init; }
    public uint? ToY { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/walkto</c>. Pathfinds on <c>Map</c>'s grid.</summary>
public sealed record WalkToRequest
{
    public uint? FromX { get; init; }
    public uint? FromY { get; init; }
    public uint? ToX { get; init; }
    public uint? ToY { get; init; }
    public string? Map { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/gm</c>. The '&' prefix is added if omitted.</summary>
public sealed record GmRequest
{
    public string? Command { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/use-gate</c>. <c>DestMap</c> is only needed
/// for multi-destination gates (the map short-name to pick).</summary>
public sealed record UseGateRequest
{
    public ushort? GateHandle { get; init; }
    public string? DestMap { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/travelto</c>. <c>To</c> is the destination
/// map short-name; <c>UnitsPerSec</c> optionally overrides the walk speed (default 120).</summary>
public sealed record TravelToRequest
{
    public string? To { get; init; }
    public double? UnitsPerSec { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/townportal</c>. <c>Dest</c> is the
/// TownPortal-table destination index (e.g. RouN: 0=RouN,1=RouVal01,2=Eld).</summary>
public sealed record TownPortalRequest
{
    public ushort? NpcHandle { get; init; }
    public byte? Dest { get; init; }
}

/// <summary>
/// Spawn request as the HTTP client sends it. Password may be supplied plaintext
/// (<see cref="Password"/>, MD5-hashed here) or pre-hashed (<see cref="PasswordMd5"/>).
/// Character creation is opt-in: set <see cref="Create"/> (or just <see cref="CharName"/>).
/// </summary>
public sealed record SpawnBotRequest
{
    public string? Host { get; init; }
    public int? LoginPort { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? PasswordMd5 { get; init; }
    public byte? WorldNo { get; init; }
    public byte? Slot { get; init; }
    /// <summary>Character to enter BY NAME (stable; preferred over <see cref="Slot"/>). Picking by
    /// slot/first-avatar logs into the wrong char when a retired char still holds an earlier slot.</summary>
    public string? Character { get; init; }
    public string? DataDir { get; init; }
    public int? WmPortFallback { get; init; }
    public string? Id { get; init; }

    // Optional in-band character creation (used only if the slot is empty).
    public bool Create { get; init; }
    public string? CharName { get; init; }
    public string? Class { get; init; }
    public byte? Gender { get; init; }

    // Optional buff-in-town behavior. Enable with `buff:true`; skill IDs are the
    // (learnt) buff skills to cast on request — empty until the priest learns them.
    public bool Buff { get; init; }
    public string? BuffTrigger { get; init; }
    public ushort[]? BuffSkillIds { get; init; }
    public bool BuffAutoNearby { get; init; }

    /// <summary>Log every inbound frame on both links (zone + WM) for introspection.</summary>
    public bool LogInbound { get; init; }

    /// <summary>Start the tailable packet dump from the first connection (captures the login +
    /// zone-enter burst, not just post-spawn). Same file as the /packetlog endpoint.</summary>
    public bool PacketLog { get; init; }

    public BotSpawnOptions ToOptions()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("host is required", nameof(Host));
        if (string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException("username is required", nameof(Username));

        BotCredentials creds = !string.IsNullOrEmpty(PasswordMd5)
            ? new BotCredentials(Username!, PasswordMd5!)
            : !string.IsNullOrEmpty(Password)
                ? BotCredentials.FromPlaintext(Username!, Password!)
                : throw new ArgumentException("password or passwordMd5 is required", nameof(Password));

        CharacterSpec? createSpec = null;
        if (Create || !string.IsNullOrWhiteSpace(CharName))
        {
            var name = string.IsNullOrWhiteSpace(CharName)
                ? $"Bot{Random.Shared.Next(1000, 9999)}" : CharName!;
            if (!Enum.TryParse<ClassId>(Class ?? nameof(ClassId.Fighter), ignoreCase: true, out var cls))
                throw new ArgumentException($"unknown class '{Class}'", nameof(Class));
            createSpec = new CharacterSpec(name, cls, Gender: Gender ?? 0, Slot: Slot ?? 0);
        }

        return new BotSpawnOptions
        {
            Host = Host!,
            LoginPort = LoginPort ?? 9010,
            Credentials = creds,
            WorldNo = WorldNo ?? 0,
            Slot = Slot,
            Character = string.IsNullOrWhiteSpace(Character) ? null : Character,
            CreateSpec = createSpec,
            // Per-bot ressystem dir for the [1801] zone-entry checksums. Precedence: the spawn's own
            // DataDir → the CLIENT_DATA_DIR env (how the in-cluster host points at the NFS-mounted client
            // data) → the local dev-machine default. Without the env fallback, an in-cluster spawn that
            // omits DataDir hit the (nonexistent) Z: path and couldn't enter a zone.
            DataDir = !string.IsNullOrWhiteSpace(DataDir) ? DataDir!
                : Environment.GetEnvironmentVariable("CLIENT_DATA_DIR") is { Length: > 0 } cdd ? cdd
                : "Z:/ClientProd2/ressystem",
            WmPortFallback = WmPortFallback ?? 9013,
            Id = string.IsNullOrWhiteSpace(Id) ? null : Id,
            Buff = Buff ? new BuffConfig
            {
                Trigger = string.IsNullOrWhiteSpace(BuffTrigger) ? "buff" : BuffTrigger!,
                SkillIds = BuffSkillIds ?? [],
                AutoBuffNearby = BuffAutoNearby,
            } : null,
            LogInbound = LogInbound,
            PacketLog = PacketLog,
        };
    }
}
