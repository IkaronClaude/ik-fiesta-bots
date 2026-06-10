using Fiesta.Bot.Login;
using Fiesta.Bot.Net;

namespace Fiesta.Bot.Host;

/// <summary>
/// Throwaway CLI to drive the login→WM chain against a live server while we
/// build it out. Usage:
///   dotnet run --project src/Fiesta.Bot.Host -- login-test \
///       --host 62.171.171.24 --port 9010 --user testuser --pass test123 [--world 0] [--slot N]
/// XOR table comes from XOR_TABLE_PATH / XOR_TABLE_HEX (BYO, never committed).
/// </summary>
public static class LoginTestCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opt = ParseArgs(args);
        var host = opt.GetValueOrDefault("host", "62.171.171.24");
        var port = int.Parse(opt.GetValueOrDefault("port", "9010"));
        var user = opt.GetValueOrDefault("user", "testuser");
        var worldNo = byte.Parse(opt.GetValueOrDefault("world", "0"));
        byte? slot = opt.TryGetValue("slot", out var s) ? byte.Parse(s) : null;

        // Optional character creation: --create [--char-name X] [--class Fighter] [--gender 0]
        CharacterSpec? createSpec = null;
        if (opt.ContainsKey("create") || opt.ContainsKey("char-name"))
        {
            var cname = opt.GetValueOrDefault("char-name", $"Bot{Random.Shared.Next(1000, 9999)}");
            var cls = Enum.TryParse<ClassId>(opt.GetValueOrDefault("class", "Fighter"), true, out var c) ? c : ClassId.Fighter;
            var gender = byte.Parse(opt.GetValueOrDefault("gender", "0"));
            createSpec = new CharacterSpec(cname, cls, Gender: gender, Slot: slot ?? 0);
        }

        // Password: --pass = plaintext (MD5'd here), or --passmd5 = already hashed.
        BotCredentials creds = opt.TryGetValue("passmd5", out var md5)
            ? new BotCredentials(user, md5)
            : BotCredentials.FromPlaintext(user, opt.GetValueOrDefault("pass", "test123"));

        byte[] table;
        try { table = XorTableLoader.Require(); }
        catch (Exception ex) { Console.Error.WriteLine($"XOR table: {ex.Message}"); return 2; }

        void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}");
        var chain = new LoginChain(table, Log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        try
        {
            var login = await chain.RunLoginAsync(new FiestaEndpoint(host, port), creds, worldNo, cts.Token);
            Log($"[ok] OTP acquired, WM advertised at {login.WmAdvertised}");

            // The WM is usually reachable at the same public host; honor the
            // advertised port. (k8s/proxy may advertise an internal IP.)
            var wmEp = new FiestaEndpoint(host, login.WmAdvertised.Port == 0 ? 9013 : login.WmAdvertised.Port);
            var (wm, wmConn) = await chain.RunWmAsync(wmEp, creds, login.Otp, slot, createSpec, cts.Token);
            using (wmConn)
            {
                Log($"[ok] WM handle={wm.WmHandle}, avatars={wm.Avatars.Count}, " +
                    (wm.ZoneAdvertised is { } z ? $"zone={z}" : "no zone (no avatar / not entered)"));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            d[key] = val;
        }
        return d;
    }
}
