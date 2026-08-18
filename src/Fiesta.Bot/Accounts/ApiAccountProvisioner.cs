using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fiesta.Bot.Login;

namespace Fiesta.Bot.Accounts;

/// <summary>Provisions a bot account through ik-fiesta-api's master-key path: POST /api/accounts with an X-Api-Key header,…</summary>
public sealed class ApiAccountProvisioner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>Client whose is the ik-fiesta-api root</summary>
    public ApiAccountProvisioner(HttpClient http, string apiKey)
    {
        if (http.BaseAddress is null)
            throw new ArgumentException("HttpClient.BaseAddress (the ik-fiesta-api root) must be set", nameof(http));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("an API key is required", nameof(apiKey));
        _http = http;
        _apiKey = apiKey;
    }

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

    // Wire shapes (mirror ik-fiesta-api's AccountModels; only the fields we use)
    private sealed record CreateAccountBody(
        string Username, string WebPassword, string IngamePassword, string? Email, int? IngameGmLevel);
    private sealed record AccountBody(int UserNo, string Username, string? Email, DateTime Created);

    /// <summary>tUser.nAuthID for a full in-game GM/admin account (what the API's admin role keys off)</summary>
    public const int GmAuthLevel = 9;
}

/// <summary>A freshly provisioned account and the credentials to log it in</summary>
public sealed record ProvisionedAccount(int UserNo, string Username, BotCredentials Credentials);

public class AccountProvisionException : Exception
{
    public AccountProvisionException(string message) : base(message) { }
}

public sealed class AccountExistsException : AccountProvisionException
{
    public AccountExistsException(string username) : base($"account '{username}' already exists") { }
}
