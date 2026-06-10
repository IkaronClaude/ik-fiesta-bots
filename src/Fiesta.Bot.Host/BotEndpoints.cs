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
    }
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
        };
    }
}
