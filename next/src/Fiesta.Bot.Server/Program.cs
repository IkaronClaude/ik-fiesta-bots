using System.Text.Json;
using System.Text.Json.Serialization;
using Fiesta.Bot.Fleet;
using Fiesta.Bot.Net;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Server;

if (args.Length > 0 && args[0] == "login-test") return await LoginTestCli.RunAsync(args);
if (args.Length > 0 && args[0] == "shn") return ShnCli.Run(args);
if (args.Length > 0 && args[0] == "bench") return await BenchCli.RunAsync(args);

// Host for the redesigned runtime (API_SPEC section 8 and section 13). Configuration (environment):
//   XOR_TABLE_PATH / XOR_TABLE_HEX   BYO cipher table (required)
//   CLIENT_DATA_DIR                  the client's ressystem directory (tables, checksums)
//   CLIENT_ROOT                      the client root (resmap block files); default: the ressystem's parent
//   BOT_DATA_DIR                     brains, fleet, checkpoints, logs, recordings (never inside the repository)
//   FIESTA_WIRE                      default packet set for new bots: 2016 (default) or 2026
//   DEFAULT_LOGIN                    host:port pre-filled in the spawn form (default 127.0.0.1:9010)
//   BOT_API_TOKEN                    bearer token (optional; also accepted as ?token= on the WebSocket)
//   UI_DIR                           the built UI (default next/ui/dist next to the solution)
//   TICK_WORKERS                     tick worker threads (default cores - 1)
var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
var xor = XorTableLoader.FromEnvironment() ?? throw new InvalidOperationException("set XOR_TABLE_PATH or XOR_TABLE_HEX (the BYO cipher table)");
var res = Environment.GetEnvironmentVariable("CLIENT_DATA_DIR") ?? "Z:/Client2026patched_10_6_6/ressystem";
var root = Environment.GetEnvironmentVariable("CLIENT_ROOT") ?? Path.GetDirectoryName(res.TrimEnd('/', '\\'))!;
var dataDir = Environment.GetEnvironmentVariable("BOT_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fiesta-bot-next");
var login = (Environment.GetEnvironmentVariable("DEFAULT_LOGIN") ?? "127.0.0.1:9010").Split(':');
var host = new HostServices
{
    XorTable = xor, RessystemDir = res, ClientRoot = root, DataDir = dataDir,
    DefaultWire = WireProfile.Parse(null),
    DefaultLogin = new Fiesta.Bot.Net.Endpoint(login[0], login.Length > 1 ? int.Parse(login[1]) : 9010),
}.Start(int.TryParse(Environment.GetEnvironmentVariable("TICK_WORKERS"), out var tw) ? tw : Math.Max(1, Environment.ProcessorCount - 1));
var supervisor = new BotSupervisor(host);
builder.Services.AddSingleton(host);
builder.Services.AddSingleton(supervisor);
builder.Services.AddSingleton<WebSocketHub>();
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
var token = Environment.GetEnvironmentVariable("BOT_API_TOKEN");
app.Use(async (ctx, next) =>
{
    if (token is { Length: > 0 } && ctx.Request.Path.StartsWithSegments("/api"))
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth != $"Bearer {token}" && ctx.Request.Query["token"] != token)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { type = "about:blank", title = "missing or wrong bearer token", status = 401 });
            return;
        }
    }
    await next();
});
var api = app.MapGroup("/api/v3");
api.MapFleetEndpoints();
api.MapBrainEndpoints();
api.MapObservationEndpoints();
app.MapWebSocketHub();
app.MapUi();
app.Lifetime.ApplicationStopping.Register(() => supervisor.DrainAsync().Wait(TimeSpan.FromSeconds(20)));
app.Lifetime.ApplicationStopped.Register(() => host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)));
app.Run();
return 0;
