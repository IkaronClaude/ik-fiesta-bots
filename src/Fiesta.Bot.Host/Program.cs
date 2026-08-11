// ik-fiesta-bots host — ASP.NET minimal API + multi-bot manager.
// Health + Swagger + the bot control surface (spawn/list/status/stop). Behaviors
// (buff/party/gear) land on top of the running BotSessions. See PROJECT_PLAN.md.
using System.Security.Cryptography;
using Fiesta.Bot.Accounts;
using Fiesta.Bot.Host;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Net;
using Fiesta.Bot.Scripting;

// Subcommand: `login-test` drives the typed login chain against a live server.
if (args.Length > 0 && args[0] == "login-test")
    return await LoginTestCli.RunAsync(args[1..]);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// The BotManager needs the BYO XOR table (cipher for the C→S link). If it's not
// configured the host still starts — the bot endpoints just return 503 with the
// reason — so /health and Swagger stay useful in a misconfigured environment.
byte[]? xorTable = null;
string? xorError = null;
try
{
    xorTable = XorTableLoader.FromEnvironment();
    if (xorTable is null)
        xorError = "No XOR table configured. Set XOR_TABLE_HEX or XOR_TABLE_PATH (BYO; not shipped).";
}
catch (Exception ex) { xorError = ex.Message; }

if (xorTable is not null)
    builder.Services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Bots");
        // BYO client data dir for SHN game-data reads (skill/item/class tables). Same
        // default as a bot's --data-dir; override with CLIENT_DATA_DIR. A real client
        // reads these files, so the bot may too (client SHNs only — see the PROJECT_PLAN
        // data-source boundary).
        var clientDataDir = Environment.GetEnvironmentVariable("CLIENT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(clientDataDir)) clientDataDir = "Z:/ClientProd2/ressystem";
        return new BotManager(xorTable, m => logger.LogInformation("{BotLog}", m))
        {
            // Let navigation actions (follow) pathfind over the BYO block grids.
            GridProvider = BotEndpoints.LoadGrid,
            DoorProvider = BotEndpoints.LoadDoors,
            AreaProvider = BotEndpoints.LoadAreas,
            ClientData = new Fiesta.Bot.GameData.ClientData(clientDataDir),
        };
    });

// Optional account provisioning via ik-fiesta-api (master-key path). Enabled only
// when both the API base URL and key are present — otherwise the endpoint 503s.
// Bots also accept credentials fed directly to spawn, so this is opt-in.
var apiBaseUrl = Environment.GetEnvironmentVariable("FIESTA_API_BASE_URL");
var apiKey = Environment.GetEnvironmentVariable("FIESTA_API_KEY");
string? provisionerError = (apiBaseUrl, apiKey) switch
{
    (null or "", _) => "FIESTA_API_BASE_URL is not set.",
    (_, null or "") => "FIESTA_API_KEY is not set.",
    _ when !Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out _) => $"FIESTA_API_BASE_URL '{apiBaseUrl}' is not an absolute URL.",
    _ => null,
};
if (provisionerError is null)
    builder.Services.AddSingleton(_ => new ApiAccountProvisioner(
        new HttpClient { BaseAddress = new Uri(apiBaseUrl!) }, apiKey!));

// The behaviour-script library (uploaded Lua, applied to bots). Always available —
// it's just storage; applying to a bot needs the manager (else those endpoints 503).
builder.Services.AddSingleton<ScriptStore>();

var app = builder.Build();

// Bearer-token auth for the control API. CRITICAL now the host is exposed on a public IP:
// without this, anyone can list/spawn/stop/drive bots. Enforced on /api/* ONLY when a
// BOT_API_TOKEN is configured (the cluster sets it via the bot-secrets secret); unset = open,
// so the local dev host stays frictionless. /health + the OpenAPI docs stay open.
var botApiToken = Environment.GetEnvironmentVariable("BOT_API_TOKEN");
if (!string.IsNullOrWhiteSpace(botApiToken))
{
    var expected = $"Bearer {botApiToken}";
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")
            && !CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()),
                   System.Text.Encoding.UTF8.GetBytes(expected)))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized. Provide 'Authorization: Bearer <token>'." });
            return;
        }
        await next();
    });
    app.Logger.LogInformation("Control API auth ENABLED (BOT_API_TOKEN set) — /api/* requires a Bearer token.");
}
else
{
    app.Logger.LogWarning("Control API auth DISABLED (no BOT_API_TOKEN) — /api/* is OPEN. Fine for local dev; DO NOT expose publicly.");
}

app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ik-fiesta-bot API"));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "ik-fiesta-bot",
    botsEnabled = xorTable is not null,
    botsDisabledReason = xorTable is null ? xorError : null,
    provisioningEnabled = provisionerError is null,
    provisioningDisabledReason = provisionerError,
}))
   .WithTags("Meta")
   .WithSummary("Liveness probe");

// --- Public bot status: a super-simple live view for bots.ikaron.uk (P1, operator 2026-07-28).
// NOT under /api, so it needs no token — it exposes only non-sensitive summary fields (level, map,
// class, phase, hp), never creds or control. Page polls /status.json. (TODO follow-ups: class-name
// mapping via ClientData, party grouping + non-bot players — tracked in tickets.md.)
var statusMgr = app.Services.GetService<BotManager>();
app.MapGet("/status.json", () =>
{
    var bots = statusMgr?.List().Select(b =>
    {
        var s = b.Snapshot();
        // clsName: resolve the ClassName.shn ClassID → English name for the status page (operator P1);
        // clients render clsName and fall back to the raw cls if the client data isn't loaded.
        return new { id = s.Id, character = s.Character, level = s.Level, cls = s.Class,
                     clsName = s.Class is { } cid ? statusMgr?.ClientData?.ClassName(cid) : null,
                     map = s.Map, phase = s.Phase, dead = s.Dead, hp = s.Hp, maxHp = s.MaxHp };
    }) ?? Enumerable.Empty<object>();
    return Results.Ok(bots);
}).WithTags("Meta").WithSummary("Public bot status (no auth)");

app.MapGet("/", () => Results.Content(StatusPage.Html, "text/html; charset=utf-8")).ExcludeFromDescription();
app.MapGet("/watch", () => Results.Content(WatchPage.Html, "text/html; charset=utf-8")).ExcludeFromDescription();

app.MapBotEndpoints(app.Services.GetService<BotManager>(), xorError);
app.MapAccountEndpoints(app.Services.GetService<ApiAccountProvisioner>(), provisionerError);
app.MapScriptEndpoints(app.Services.GetService<BotManager>(),
    app.Services.GetRequiredService<ScriptStore>(), xorError);

// BYO client game-data inspection (read-only). Confirms an operator-supplied client
// SHN loads and surfaces the data feature code reads (e.g. ActiveSkill fields the cast
// keys off). 503 if no client data dir / bot manager; 404 if the table/skill is absent.
var gameData = app.MapGroup("/api/gamedata").WithTags("GameData");
IResult NoClientData() => Results.Problem(
    title: "Client game-data unavailable",
    detail: xorError ?? "No client data dir configured (set CLIENT_DATA_DIR; BYO ressystem).",
    statusCode: StatusCodes.Status503ServiceUnavailable);

gameData.MapGet("/{table}", (string table) =>
{
    var cd = app.Services.GetService<BotManager>()?.ClientData;
    if (cd is null) return NoClientData();
    var t = cd.Table(table);
    return t is null
        ? Results.NotFound(new { error = $"client table '{table}.shn' not found in {cd.DataDir}" })
        : Results.Ok(new { table = t.Name, rows = t.Rows.Count,
            columns = t.Columns.Select(c => new { c.Name, type = c.Type.ToString() }) });
})
.WithSummary("Inspect a BYO client SHN table (row count + columns) — confirms it loads");

gameData.MapGet("/skill/{skillId:int}", (int skillId) =>
{
    var cd = app.Services.GetService<BotManager>()?.ClientData;
    if (cd is null) return NoClientData();
    var s = cd.Skill(skillId);
    return s is null
        ? Results.NotFound(new { error = $"skill {skillId} not in ActiveSkill (or table missing)" })
        : Results.Ok(s);
})
.WithSummary("Read an ActiveSkill row's combat fields (facing/cooldown/range/mana) from BYO client data");

// ROSTER RESTORE (tickets.md P0, 2026-08-11): bring back every bot that was running when the process
// last died. BotManager.Spawn persists its options; an explicit StopAsync de-persists them; a crash, an
// OOM or a deploy does not — so whatever is left here is exactly "what should be running".
// WHY THIS EXISTS: the host restarted FOUR times on 2026-08-11 (three deploys + a node OOM) and every
// time came back with an EMPTY roster — `GET /api/bots` returning `[]`, nobody logged in, and nothing
// announcing it. The script watchdog already restored WHAT each bot runs; nothing restored THAT it runs.
// Staggered, because five logins landing together is when the zone-connect drops were seen; and each
// failure is logged with its id, since a silent restore failure is the same invisibility all over again.
{
    var restoreMgr = app.Services.GetService<BotManager>();
    if (restoreMgr is not null)
    {
        var saved = restoreMgr.Knowledge.LoadRoster();
        if (saved.Count > 0)
        {
            app.Logger.LogInformation("Startup: restoring {Count} bot(s) from the persisted roster…", saved.Count);
            _ = Task.Run(async () =>
            {
                foreach (var (id, opts) in saved)
                {
                    try
                    {
                        restoreMgr.Spawn(opts with { Id = id });
                        app.Logger.LogInformation("Startup: restored bot {Id}", id);
                    }
                    catch (Exception ex)
                    {
                        app.Logger.LogWarning("Startup: could NOT restore bot {Id}: {Err}", id, ex.Message);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            });
        }
    }
}

// GRACEFUL SHUTDOWN (tickets.md P3): on SIGTERM (a deploy/pod restart), cleanly LOG OUT every running
// bot before the process exits. StopAsync sends the game quit frames (LOGOUTREADY+quit, WM quit, 3s cap
// each) — which makes the server DROP the zone session immediately. Without this, a hard pod-kill leaves
// the connection dropped-but-not-closed → the server holds a GHOST session for many minutes → the respawn
// gets `cancelled before zone entry`. Bounded to ~20s to stay inside the pod's terminationGracePeriod (30s).
{
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var shutdownMgr = app.Services.GetService<BotManager>();
    lifetime.ApplicationStopping.Register(() =>
    {
        var bots = shutdownMgr?.List().ToList();
        if (bots is not { Count: > 0 }) return;
        app.Logger.LogInformation("Shutdown: cleanly logging out {Count} bot(s) to avoid ghost sessions…", bots.Count);
        try { Task.WhenAll(bots.Select(b => shutdownMgr!.StopAsync(b.Id, default, forget: false))).Wait(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Shutdown: bot logout hit an error (continuing)."); }
        app.Logger.LogInformation("Shutdown: bot logout complete.");
    });
}

app.Run();
return 0;
