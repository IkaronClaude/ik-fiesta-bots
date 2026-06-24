using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Manager;
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

        group.MapGet("/{id}/log", (string id, string? level, int? max) =>
        {
            var bot = manager.Get(id);
            if (bot is null) return Results.NotFound();
            var maxLevel = (level?.ToLowerInvariant()) switch
            {
                "note" or "n" => BotLogLevel.Note,
                "info" or "i" => BotLogLevel.Info,
                _ => BotLogLevel.Verbose,   // default: the full firehose
            };
            var lines = bot.RecentLines(max ?? 200, maxLevel);
            return Results.Text(string.Join("\n", lines) + "\n", "text/plain");
        })
        .WithSummary("Tail a bot's log as plain text, filtered by verbosity")
        .WithDescription("Query: level=note|info|verbose (default verbose=everything), max=N lines (default 200). " +
            "note=headline only (quest accept/finish, level-up, death, purchase, errors); info adds kills/quest-progress; " +
            "verbose adds move/cast/auto-attack. Plain text so `curl .../log?level=info` is directly readable.");

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
            var inv = bot.ZoneView?.Inventory;
            if (inv is null) return Results.Conflict(new { error = "bot is not in zone yet" });
            return Results.Ok(new { id, items = inv.OrderBy(kv => kv.Key).Select(kv => new { slot = kv.Key, itemId = kv.Value }) });
        })
        .WithSummary("List the bot's bag contents (slot → itemId)");

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
                q.NeedsNpc, q.NeedsItem, q.NeedsItemId, q.NeedsClass, q.IsVisible,
                remoteAcceptable = q.IsInstantAccept, q.IsInstantHandIn, q.Region, q.QuestType, q.Repeatable,
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
        try { return File.Exists(path) ? BlockGrid.Load(path) : null; }
        catch { return null; }
    });

    private static IResult ToResult(BotManager.ActionResult result, string id, object ok) => result switch
    {
        BotManager.ActionResult.Sent => Results.Ok(ok),
        BotManager.ActionResult.NotInZone => Results.Conflict(new { error = "bot is not in zone yet" }),
        _ => Results.NotFound(),
    };
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
            CreateSpec = createSpec,
            DataDir = string.IsNullOrWhiteSpace(DataDir) ? "Z:/ClientProd2/ressystem" : DataDir!,
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
