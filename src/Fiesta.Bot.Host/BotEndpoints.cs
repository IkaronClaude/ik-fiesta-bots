using Fiesta.Bot.Behaviors;
using Fiesta.Bot.Login;
using Fiesta.Bot.Manager;

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
            group.MapPost("/{id}/use-item", (string id) => Unavailable()).WithSummary("Bot use-item (unavailable)");
            group.MapPost("/{id}/whisper", (string id) => Unavailable()).WithSummary("Bot whisper (unavailable)");
            group.MapGet("/{id}/inventory", (string id) => Unavailable()).WithSummary("Bot inventory (unavailable)");
            group.MapGet("/{id}/equipment", (string id) => Unavailable()).WithSummary("Bot equipment (unavailable)");
            group.MapPost("/{id}/equip", (string id) => Unavailable()).WithSummary("Bot equip (unavailable)");
            group.MapPost("/{id}/gm", (string id) => Unavailable()).WithSummary("Bot GM command (unavailable)");
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

        group.MapPost("/{id}/stop", async (string id) =>
        {
            var stopped = await manager.StopAsync(id);
            return stopped ? Results.Ok(new { id, stopped = true }) : Results.NotFound();
        })
        .WithSummary("Stop a bot and remove it from the manager");

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

        group.MapPost("/{id}/use-item", async (string id, UseItemRequest req) =>
        {
            if (req.Slot is not { } slot)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slot"] = ["inventory slot is required"] });
            return ToResult(await manager.UseItemAsync(id, slot, req.InvenType ?? 0), id, new { id, usedSlot = slot });
        })
        .WithSummary("Use an inventory item by slot");

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

        group.MapPost("/{id}/equip", async (string id, EquipRequest req) =>
        {
            if (req.Slot is not { } slot)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slot"] = ["inventory slot is required"] });
            return ToResult(await manager.EquipAsync(id, slot), id, new { id, equippedFromSlot = slot });
        })
        .WithSummary("Equip the inventory item at the given slot");

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
    }

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

/// <summary>Body for <c>POST /api/bots/{id}/cast</c>.</summary>
public sealed record CastRequest
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

/// <summary>Body for <c>POST /api/bots/{id}/whisper</c>.</summary>
public sealed record WhisperRequest
{
    public string? To { get; init; }
    public string? Text { get; init; }
}

/// <summary>Body for <c>POST /api/bots/{id}/gm</c>. The '&' prefix is added if omitted.</summary>
public sealed record GmRequest
{
    public string? Command { get; init; }
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
        };
    }
}
