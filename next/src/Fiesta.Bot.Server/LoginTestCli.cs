using Fiesta.Bot.Net;
using Fiesta.Bot.Protocol;

namespace Fiesta.Bot.Server;

/// <summary>`login-test`: log one character in through a wire profile, enter the zone, count decoded events, log out.</summary>
public static class LoginTestCli
{
    /// <summary>Run.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        string Arg(string k, string d) { var i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
        var wire = WireProfile.For(WireProfile.Parse(Arg("--wire", null!)));
        var host = Arg("--host", "127.0.0.1"); var port = int.Parse(Arg("--port", "9010"));
        var user = Arg("--user", "r_nextbot1"); var pass = Arg("--pass", "nextbot1");
        var chr = Arg("--char", ""); var cls = Arg("--class", "");
        var data = Arg("--data-dir", Environment.GetEnvironmentVariable("CLIENT_DATA_DIR") ?? "Z:/Client2026patched_10_6_6/ressystem");
        var hold = int.Parse(Arg("--hold", "10"));
        var xor = XorTableLoader.FromEnvironment() ?? throw new InvalidOperationException("set XOR_TABLE_PATH");
        void Log(string s) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {s}");
        var req = new LoginRequest(new Fiesta.Bot.Net.Endpoint(host, port), user, Ascii.Md5Hex(pass), chr.Length > 0 ? chr : null, cls.Length > 0, cls.Length > 0 ? int.Parse(cls) : null) { Log = Log };
        var res = await wire.CreateLogin(xor).RunAsync(req, CancellationToken.None);
        Log($"login ok: zone {res.Zone} char {res.CharacterName} lv {res.Level} class {res.ClassId} map {res.LoginMap}");
        var sums = wire.ComputeChecksums(data);
        var sink = new CountingSink();
        var outb = new ChannelOutbound();
        await using var zs = new ZoneSession(wire, DecoderTable.CreateDefault(wire), xor, sink, outb, sums, () => Environment.TickCount64, Log);
        using var cts = new CancellationTokenSource();
        var run = zs.RunAsync(res, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(hold));
        await zs.LogoutAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        cts.Cancel();
        try { await run; } catch { }
        foreach (var kv in sink.Counts.OrderByDescending(k => k.Value)) Log($"  {kv.Key}: {kv.Value}");
        foreach (var kv in wire.Mismatches) Log($"  shape mismatch 0x{kv.Key:X4} x{kv.Value}");
        return 0;
    }

    private sealed class CountingSink : IEventSink
    {
        public readonly Dictionary<InboundKind, int> Counts = new();
        public bool TryWrite(in InboundEvent evt)
        {
            lock (Counts) Counts[evt.Kind] = Counts.GetValueOrDefault(evt.Kind) + 1;
            if (evt.Kind is InboundKind.MapLoginAck or InboundKind.SelfBase or InboundKind.MapLoginFail or InboundKind.MapLink)
                Console.WriteLine($"   event {evt.Kind} h={evt.Handle} A={evt.A} B={evt.B} C={evt.C} xy=({evt.X},{evt.Y}) '{evt.Text}'");
            return true;
        }
    }
}

/// <summary>`shn DIR TABLE[:rows]...`: print a client table's columns (and first rows).</summary>
public static class ShnCli
{
    /// <summary>Run.</summary>
    public static int Run(string[] args)
    {
        for (var i = 2; i < args.Length; i++)
        {
            var parts = args[i].Split(':');
            var t = Fiesta.Bot.Services.ShnTable.Load(Path.Combine(args[1], parts[0] + ".shn"));
            Console.WriteLine($"== {t.Name} rows={t.RowCount}: " + string.Join(", ", t.Columns.Select((c, k) => $"{c}:{t.Types[k]}")));
            var n = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            for (var r = 0; r < Math.Min(n, t.RowCount); r++)
                Console.WriteLine("   " + string.Join(" | ", t.Columns.Select((c, k) => t.Str(r, k) ?? t.Num(r, k).ToString())));
        }
        return 0;
    }
}
