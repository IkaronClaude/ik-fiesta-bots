// ik-fiesta-bots host — ASP.NET minimal API + multi-bot manager
using System.Security.Cryptography;
using Fiesta.Bot.Accounts;
using Fiesta.Bot.Host;
using Fiesta.Bot.GameData;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Net;
using Fiesta.Bot.Pathfinding;
using Fiesta.Bot.Scripting;

// Subcommand: `login-test` drives the typed login chain against a live server
if (args.Length > 0 && args[0] == "login-test")
    return await LoginTestCli.RunAsync(args[1..]);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// The BotManager needs the BYO XOR table (cipher for the C→S link)
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
        // BYO client data dir for SHN game-data reads (skill/item/class tables)
        var clientDataDir = Environment.GetEnvironmentVariable("CLIENT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(clientDataDir)) clientDataDir = "Z:/ClientProd2/ressystem";
        return new BotManager(xorTable, m => logger.LogInformation("{BotLog}", m))
        {
            // Let navigation actions (follow) pathfind over the BYO block grids
            GridProvider = BotEndpoints.LoadGrid,
            DoorProvider = BotEndpoints.LoadDoors,
            AreaProvider = BotEndpoints.LoadAreas,
            ClientData = new Fiesta.Bot.GameData.ClientData(clientDataDir),
        };
    });

// Optional account provisioning via ik-fiesta-api (master-key path)
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

// The behaviour-script library (uploaded Lua, applied to bots)
builder.Services.AddSingleton<ScriptStore>();

var app = builder.Build();

// BEFORE THE AUTH MIDDLEWARE
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Bearer-token auth for the control API
var botApiToken = Environment.GetEnvironmentVariable("BOT_API_TOKEN");
if (!string.IsNullOrWhiteSpace(botApiToken))
{
    var expected = $"Bearer {botApiToken}";
    app.Use(async (ctx, next) =>
    {
        // A BROWSER CANNOT SET A HEADER ON A WEBSOCKET
        var authed = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()),
            System.Text.Encoding.UTF8.GetBytes(expected));
        if (!authed && ctx.WebSockets.IsWebSocketRequest)
        {
            foreach (var proto in ctx.WebSockets.WebSocketRequestedProtocols)
            {
                if (CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(proto),
                        System.Text.Encoding.UTF8.GetBytes(botApiToken!)))
                {
                    authed = true;
                    break;
                }
            }
        }
        if (ctx.Request.Path.StartsWithSegments("/api") && !authed)
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

// --- Public bot status: a super-simple live view for bots.ikaron.uk (P1, operator 2026-07-28)
var statusMgr = app.Services.GetService<BotManager>();
app.MapGet("/status.json", () =>
{
    var bots = statusMgr?.List().Select(b =>
    {
        var s = b.Snapshot();
        // clsName: resolve the ClassName.shn ClassID → English name for the status page (operator P1); clients render cl…
        return new { id = s.Id, character = s.Character, level = s.Level, cls = s.Class,
                     clsName = s.Class is { } cid ? statusMgr?.ClientData?.ClassName(cid) : null,
                     map = s.Map, phase = s.Phase, dead = s.Dead, hp = s.Hp, maxHp = s.MaxHp };
    }) ?? Enumerable.Empty<object>();
    return Results.Ok(bots);
}).WithTags("Meta").WithSummary("Public bot status (no auth)");

app.MapGet("/", () => Results.Content(StatusPage.Html, "text/html; charset=utf-8")).ExcludeFromDescription();
app.MapGet("/watch", () => Results.Content(WatchPage.Html, "text/html; charset=utf-8")).ExcludeFromDescription();
// ITEM ICONS, cut out of the client's own atlas and served as PNGs so the watch page can draw a bag that looks l…
app.MapGet("/icon/{itemId:int}.png", (int itemId) =>
{
    var cd = app.Services.GetService<BotManager>()?.ClientData;
    var png = cd?.ItemIconPng(itemId);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
}).ExcludeFromDescription();

// SKILL ICONS — same atlas scheme as items (ActiveSkillView.shn IconFile/IconIndex), so the skill bar draws the…
app.MapGet("/skillicon/{skillId:int}.png", (int skillId) =>
{
    var cd = app.Services.GetService<BotManager>()?.ClientData;
    var png = cd?.SkillIconPng(skillId);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
}).ExcludeFromDescription();

// ABSTATE (buff/debuff) ICONS — AbStateView.shn iconFile/icon, same atlases again
app.MapGet("/abstateicon/{abStateId:int}.png", (int abStateId) =>
{
    var cd = app.Services.GetService<BotManager>()?.ClientData;
    var png = cd?.AbStateIconPng(abStateId);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
}).ExcludeFromDescription();

// MAP MINIMAPS — the client's own per-map art, so /watch shows where a bot is the way the game does instead of a…
app.MapGet("/minimap/{map}.png", (string map) =>
{
    var cd = app.Services.GetService<BotManager>()?.ClientData;
    var png = cd?.MinimapPng(map);
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
}).ExcludeFromDescription();

app.MapGet("/minimap/{map}.json", (string map) =>
{
    var mgr = app.Services.GetService<BotManager>();
    var grid = mgr?.GridProvider?.Invoke(map);
    var cd = mgr?.ClientData;
    double? ww = grid is null ? null : grid.WidthTiles * BlockGrid.WorldPerTile;
    double? wh = grid is null ? null : grid.HeightTiles * BlockGrid.WorldPerTile;
    // The WORLD RECT THE IMAGE COVERS, from the client's own MapViewInfo.shn — not the whole grid
    var rect = ww is { } w0 && wh is { } h0 ? cd?.MinimapWorldRect(map, w0, h0) : null;
    return Results.Ok(new
    {
        map,
        hasArt = cd?.MinimapDir is { } d && MinimapImage.Exists(d, map),
        worldWidth = ww,
        worldHeight = wh,
        // Fall back to the full grid when the table has no row for this map
        coverX0 = rect?.X0 ?? 0,
        coverY0 = rect?.Y0 ?? 0,
        coverX1 = rect?.X1 ?? ww,
        coverY1 = rect?.Y1 ?? wh,
        coverFromTable = rect is not null,
        flipY = true,
    });
}).ExcludeFromDescription();


app.MapBotEndpoints(app.Services.GetService<BotManager>(), xorError);
app.MapEventStream(app.Services.GetService<BotManager>());
app.MapAccountEndpoints(app.Services.GetService<ApiAccountProvisioner>(), provisionerError);
app.MapScriptEndpoints(app.Services.GetService<BotManager>(),
    app.Services.GetRequiredService<ScriptStore>(), xorError);

// BYO client game-data inspection (read-only)
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

// ROSTER RESTORE (tickets.md P0, 2026-08-11): bring back every bot that was running when the process last died
Task startupRestore = Task.CompletedTask;
{
    var restoreMgr = app.Services.GetService<BotManager>();
    if (restoreMgr is not null)
    {
        var saved = restoreMgr.Knowledge.LoadRoster();
        if (saved.Count > 0)
        {
            app.Logger.LogInformation("Startup: restoring {Count} bot(s) from the persisted roster…", saved.Count);
            startupRestore = Task.Run(async () =>
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

// ROSTER SUPERVISOR — keeps the roster whole WHILE RUNNING, not just at startup ──────────────── Startup restore…
{
    var supMgr = app.Services.GetService<BotManager>();
    if (supMgr is not null)
    {
        _ = Task.Run(async () =>
        {
            try { await startupRestore; } catch { }   // a failed restore must not silence the supervisor
            while (true)
            {
                try
                {
                    var desired = supMgr.Knowledge.LoadRoster();
                    var live = supMgr.List().ToDictionary(b => b.Id, b => b);
                    foreach (var (id, opts) in desired)
                    {
                        live.TryGetValue(id, out var h);
                        var phase = h?.Phase.ToString();   // BotPhase enum -> "Failed"/"Stopped"/"InZone"
                        var missing = h is null;
                        var broken  = phase is "Failed" or "Stopped";
                        if (missing || broken)
                        {
                            app.Logger.LogWarning("Supervisor: bot {Id} is {State} — re-establishing", id,
                                                  missing ? "MISSING from the roster" : phase);
                            if (!missing)
                            {
                                // Stop WITHOUT forgetting: this bot is still one we were asked to run, so it must stay in the restore roster
                                try { await supMgr.StopAsync(id, default, forget: false); } catch { }
                                await Task.Delay(TimeSpan.FromSeconds(3));
                            }
                            try { supMgr.Spawn(opts with { Id = id }); }
                            catch (Exception ex) { app.Logger.LogWarning("Supervisor: respawn {Id} failed: {E}", id, ex.Message); }
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                    }
                }
                catch (Exception ex) { app.Logger.LogWarning("Supervisor pass failed: {E}", ex.Message); }
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
        });
    }
}

// GRACEFUL SHUTDOWN (tickets.md P3): on SIGTERM (a deploy/pod restart), cleanly LOG OUT every running bot before…
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
