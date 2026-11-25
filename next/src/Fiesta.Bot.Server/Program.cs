using Fiesta.Bot.Server;

if (args.Length > 0 && args[0] == "login-test") return await LoginTestCli.RunAsync(args);
if (args.Length > 0 && args[0] == "shn") return ShnCli.Run(args);

// Host for the redesigned runtime (replaces Fiesta.Bot.Host after migration; API_SPEC section 8).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();
return 0;
