// ik-fiesta-bots host — ASP.NET minimal API + multi-bot manager.
// Skeleton for now: health + Swagger. Bot spawn/list/stop + behaviors land as
// the core library (Fiesta.Bot) fills in. See PROJECT_PLAN.md.
using Fiesta.Bot.Host;

// Subcommand: `login-test` drives the typed login chain against a live server.
if (args.Length > 0 && args[0] == "login-test")
    return await LoginTestCli.RunAsync(args[1..]);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ik-fiesta-bot API"));

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ik-fiesta-bot" }))
   .WithTags("Meta")
   .WithSummary("Liveness probe");

app.Run();
return 0;
