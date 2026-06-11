// ik-fiesta-bots host — ASP.NET minimal API + multi-bot manager.
// Health + Swagger + the bot control surface (spawn/list/status/stop). Behaviors
// (buff/party/gear) land on top of the running BotSessions. See PROJECT_PLAN.md.
using Fiesta.Bot.Accounts;
using Fiesta.Bot.Host;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Net;

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
        return new BotManager(xorTable, m => logger.LogInformation("{BotLog}", m))
        {
            // Let navigation actions (follow) pathfind over the BYO block grids.
            GridProvider = BotEndpoints.LoadGrid,
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

var app = builder.Build();

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

app.MapBotEndpoints(app.Services.GetService<BotManager>(), xorError);
app.MapAccountEndpoints(app.Services.GetService<ApiAccountProvisioner>(), provisionerError);

app.Run();
return 0;
