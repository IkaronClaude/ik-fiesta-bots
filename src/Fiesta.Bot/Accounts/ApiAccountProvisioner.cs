using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fiesta.Bot.Login;

namespace Fiesta.Bot.Accounts;

/// <summary>
/// Provisions a bot account through ik-fiesta-api's master-key path:
/// <c>POST /api/accounts</c> with an <c>X-Api-Key</c> header, which bypasses the
/// captcha + register rate-limit and creates a game account via the verified
/// <c>usp_User_insert</c> stored procedure (MD5 in-game password). This keeps the
/// bot off the DB schema and honours the "don't touch mssql/SA directly" guardrail.
///
/// OPTIONAL: bots also accept credentials fed directly (the CLI / HTTP spawn do).
/// This is one way to get an account, not a requirement. The returned
/// <see cref="ProvisionedAccount.Credentials"/> are exactly what the login chain
/// expects (in-game password MD5-hashed the same way the API hashes <c>sUserPW</c>).
///
/// Granting an in-game GM level on create is NOT yet supported by the API — see
/// <see cref="GmLevel"/> and the task-18 note in PROJECT_PLAN.md.
/// </summary>
public sealed class ApiAccountProvisioner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <param name="http">Client whose <see cref="HttpClient.BaseAddress"/> is the
    /// ik-fiesta-api root (e.g. https://api.example/). The caller owns its lifetime.</param>
    /// <param name="apiKey">A valid API key (minted by an admin via POST /api/apikeys).</param>
    public ApiAccountProvisioner(HttpClient http, string apiKey)
    {
        if (http.BaseAddress is null)
            throw new ArgumentException("HttpClient.BaseAddress (the ik-fiesta-api root) must be set", nameof(http));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("an API key is required", nameof(apiKey));
        _http = http;
        _apiKey = apiKey;
    }

    /// <summary>
    /// Create a game account. The in-game password is what the bot logs in with;
    /// the web password defaults to the in-game one when not given (the bot never
    /// uses the web login). <paramref name="ingameGmLevel"/> (tUser.nAuthID) is
    /// honoured because the API key marks us a trusted caller — null leaves the
    /// default (1 = normal), 9 = admin/GM. Returns the new account + ready-to-use
    /// credentials. Throws <see cref="AccountExistsException"/> on 409,
    /// <see cref="AccountProvisionException"/> otherwise.
    /// </summary>
    public async Task<ProvisionedAccount> CreateAccountAsync(
        string username, string ingamePassword, string? webPassword = null,
        string? email = null, int? ingameGmLevel = null, CancellationToken ct = default)
    {
        var body = new CreateAccountBody(username, webPassword ?? ingamePassword, ingamePassword, email, ingameGmLevel);

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/accounts")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        req.Headers.Add("X-Api-Key", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        var payload = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new AccountExistsException(username);
        if (!resp.IsSuccessStatusCode)
            throw new AccountProvisionException(
                $"POST /api/accounts → {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(payload)}");

        AccountBody? acct;
        try { acct = JsonSerializer.Deserialize<AccountBody>(payload, Json); }
        catch (JsonException ex) { throw new AccountProvisionException($"unparseable 201 body: {ex.Message}"); }
        if (acct is null)
            throw new AccountProvisionException("empty 201 body from POST /api/accounts");

        var creds = BotCredentials.FromPlaintext(username, ingamePassword);
        return new ProvisionedAccount(acct.UserNo, acct.Username, creds);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    // Wire shapes (mirror ik-fiesta-api's AccountModels; only the fields we use).
    private sealed record CreateAccountBody(
        string Username, string WebPassword, string IngamePassword, string? Email, int? IngameGmLevel);
    private sealed record AccountBody(int UserNo, string Username, string? Email, DateTime Created);

    /// <summary>tUser.nAuthID for a full in-game GM/admin account (what the API's
    /// admin role keys off). Pass as <c>ingameGmLevel</c> to self-gear via GM commands.</summary>
    public const int GmAuthLevel = 9;
}

/// <summary>A freshly provisioned account and the credentials to log it in.</summary>
public sealed record ProvisionedAccount(int UserNo, string Username, BotCredentials Credentials);

public class AccountProvisionException : Exception
{
    public AccountProvisionException(string message) : base(message) { }
}

public sealed class AccountExistsException : AccountProvisionException
{
    public AccountExistsException(string username) : base($"account '{username}' already exists") { }
}
