using Fiesta.Bot.Accounts;

namespace Fiesta.Bot.Host;

/// <summary>Optional account-provisioning surface: POST /api/accounts mints a game account through ik-fiesta-api's master-…</summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app, ApiAccountProvisioner? provisioner, string? unavailableReason)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        if (provisioner is null)
        {
            group.MapPost("/", () => Results.Problem(
                title: "Account provisioning unavailable",
                detail: unavailableReason ?? "Set FIESTA_API_BASE_URL and FIESTA_API_KEY to enable.",
                statusCode: StatusCodes.Status503ServiceUnavailable))
                .WithSummary("Provision an account (unavailable)");
            return;
        }

        group.MapPost("/", async (ProvisionAccountRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.IngamePassword))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["username and ingamePassword are required"],
                });

            try
            {
                var acct = await provisioner.CreateAccountAsync(
                    req.Username!, req.IngamePassword!, req.WebPassword, req.Email, req.IngameGmLevel, ct);
                return Results.Created($"/api/accounts/{acct.UserNo}", new ProvisionAccountResponse(
                    acct.UserNo, acct.Username, acct.Credentials.Username, acct.Credentials.PasswordMd5));
            }
            catch (AccountExistsException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (AccountProvisionException ex) { return Results.Problem(
                title: "Account provisioning failed", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway); }
        })
        .WithSummary("Provision a game account via ik-fiesta-api")
        .WithDescription("Creates an account (master-key path) and returns credentials ready for POST /api/bots.");
    }
}

/// <summary>Provision request. The in-game password is what the bot logs in with; the web password defaults to it (the bot…</summary>
public sealed record ProvisionAccountRequest
{
    public string? Username { get; init; }
    public string? IngamePassword { get; init; }
    public string? WebPassword { get; init; }
    public string? Email { get; init; }

    /// <summary>In-game GM/auth level (tUser.nAuthID) to provision with: null = normal player, 9 = GM/admin (see )</summary>
    public int? IngameGmLevel { get; init; }
}

/// <summary>The new account plus credentials to feed straight into POST /api/bots</summary>
public sealed record ProvisionAccountResponse(int UserNo, string Username, string LoginUsername, string PasswordMd5);
