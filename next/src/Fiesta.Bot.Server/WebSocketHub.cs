using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Fleet;
using Fiesta.Bot.Services.Logging;
using LogLevel = Fiesta.Bot.Services.Logging.LogLevel;

namespace Fiesta.Bot.Server;

/// <summary>WebSocket topics (API_SPEC 8.4).</summary>
public enum WsTopic : byte { Fleet, Debug, Transition, Sensors, Log, Alert, Game }

/// <summary>One subscription from a client.</summary>
public sealed class WsSubscription(WsTopic topic, string? bot, int hz, LogLevel level)
{
    /// <summary>Topic.</summary>
    public WsTopic Topic { get; } = topic;
    /// <summary>Bot (null = all).</summary>
    public string? Bot { get; } = bot;
    /// <summary>Rate for sensors.</summary>
    public int Hz { get; } = Math.Clamp(hz, 1, 10);
    /// <summary>Log level for the log topic.</summary>
    public LogLevel Level { get; } = level;
    /// <summary>Previous frame for deltas.</summary>
    public JsonNode? Prev;
    /// <summary>Last send.</summary>
    public long LastSent;
    /// <summary>Log lines this second.</summary>
    public int LinesThisSecond; public long Second;
}

/// <summary>The /api/v3/ws hub: subscriptions, delta encoding (RFC 6902 patches), fan-out off the tick threads.</summary>
public sealed class WebSocketHub
{
    private sealed class Conn
    {
        public readonly Channel<string> Out = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        public readonly List<WsSubscription> Subs = new();
        public readonly List<(GameStream Stream, GameViewer Viewer)> Games = new();
    }
    private readonly ConcurrentDictionary<string, GameStream> _games = new();

    /// <summary>The game streams (for status / measurement).</summary>
    public IEnumerable<GameStream> GameStreams => _games.Values;
    private readonly ConcurrentDictionary<Conn, byte> _conns = new();
    private readonly BotSupervisor _sup;
    private readonly HashSet<BotRuntime> _hooked = new();

    /// <summary>Create (wires the supervisor's events).</summary>
    public WebSocketHub(BotSupervisor sup)
    {
        _sup = sup;
        sup.Alert += (bot, kind, text) => Broadcast(WsTopic.Alert, bot, new { bot, kind, text });
        _ = Task.Run(PumpAsync);
    }

    private void Hook(BotRuntime b)
    {
        lock (_hooked)
        {
            if (!_hooked.Add(b)) return;
            b.FrameReady += (r, f) => Frame(r.Record.Id, f);
            b.TransitionReady += (r, t) => Broadcast(WsTopic.Transition, r.Record.Id, t);
            b.Log.Admitted += (id, e) => LogLine(id, e);
        }
    }

    private static readonly JsonSerializerOptions J = Http.Json;

    private void Frame(string bot, DebugFrame f)
    {
        JsonNode? node = null;
        foreach (var c in _conns.Keys)
            lock (c.Subs)
                foreach (var s in c.Subs)
                    if (s.Topic == WsTopic.Debug && s.Bot == bot)
                    {
                        node ??= JsonSerializer.SerializeToNode(f, J);
                        SendState(c, s, node!.DeepClone());
                    }
    }

    private void LogLine(string bot, LogEntry e)
    {
        foreach (var c in _conns.Keys)
            lock (c.Subs)
                foreach (var s in c.Subs)
                    if (s.Topic == WsTopic.Log && (s.Bot is null || s.Bot == bot) && e.Level <= s.Level)
                    {
                        var sec = Environment.TickCount64 / 1000;
                        if (s.Second != sec) { s.Second = sec; s.LinesThisSecond = 0; }
                        if (++s.LinesThisSecond > 20) continue;
                        c.Out.Writer.TryWrite(JsonSerializer.Serialize(new { topic = "log", bot, data = Http.Line(e) }, J));
                    }
    }

    private void Broadcast(WsTopic topic, string? bot, object data)
    {
        string? text = null;
        foreach (var c in _conns.Keys)
            lock (c.Subs)
                foreach (var s in c.Subs)
                    if (s.Topic == topic && (s.Bot is null || bot is null || s.Bot == bot))
                    {
                        text ??= JsonSerializer.Serialize(new { topic = topic.ToString().ToLowerInvariant(), bot, data }, J);
                        c.Out.Writer.TryWrite(text);
                    }
    }

    private static void SendState(Conn c, WsSubscription s, JsonNode data)
    {
        string msg;
        var topic = s.Topic.ToString().ToLowerInvariant();
        if (s.Prev is null)
            msg = new JsonObject { ["topic"] = topic, ["bot"] = s.Bot, ["full"] = true, ["data"] = data.DeepClone() }.ToJsonString();
        else
        {
            var patch = new JsonArray();
            JsonPatch.Diff(s.Prev, data, "", patch);
            if (patch.Count == 0) return;
            msg = new JsonObject { ["topic"] = topic, ["bot"] = s.Bot, ["full"] = false, ["patch"] = patch }.ToJsonString();
        }
        s.Prev = data;
        c.Out.Writer.TryWrite(msg);
    }

    // periodic topics: fleet (1 Hz), sensors (per subscription rate)
    private async Task PumpAsync()
    {
        while (true)
        {
            await Task.Delay(100);
            foreach (var b in _sup.Bots) Hook(b);
            var unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var g in _games.Values)
                try { g.Pump(unix, _sup.Get(g.Bot)?.State == SupervisorState.InZone); }
                catch (Exception e) { Console.Error.WriteLine($"game stream {g.Bot}: {e}"); }
            var now = Environment.TickCount64;
            JsonNode? fleet = null;
            foreach (var c in _conns.Keys)
                lock (c.Subs)
                    foreach (var s in c.Subs)
                    {
                        if (s.Topic == WsTopic.Fleet && now - s.LastSent >= 1000)
                        {
                            s.LastSent = now;
                            fleet ??= JsonSerializer.SerializeToNode(_sup.Bots.Select(Http.Summary), J);
                            SendState(c, s, fleet!.DeepClone());
                        }
                        else if (s.Topic == WsTopic.Sensors && now - s.LastSent >= 1000 / s.Hz && s.Bot is not null && _sup.Get(s.Bot)?.Sensors is { } sf)
                        {
                            s.LastSent = now;
                            SendState(c, s, JsonSerializer.SerializeToNode(sf, J)!);
                        }
                    }
        }
    }

    /// <summary>Serve one connection.</summary>
    public async Task ServeAsync(WebSocket ws, CancellationToken ct)
    {
        var c = new Conn();
        _conns[c] = 0;
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var m in c.Out.Reader.ReadAllAsync(ct))
                    await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, ct);
            }
            catch (Exception) { }
        }, CancellationToken.None);
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, ct); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                try { Handle(c, JsonDocument.Parse(ms.ToArray()).RootElement); }
                catch (JsonException) { c.Out.Writer.TryWrite("{\"topic\":\"error\",\"data\":\"not JSON\"}"); }
            }
        }
        catch (Exception) { }
        finally
        {
            _conns.TryRemove(c, out _);
            lock (c.Games) foreach (var (g, v) in c.Games) g.Remove(v);
            lock (c.Subs) foreach (var s in c.Subs) if (s.Bot is not null && s.Topic is WsTopic.Debug or WsTopic.Sensors && _sup.Get(s.Bot) is { } b) Interlocked.Decrement(ref b.Watchers);
            c.Out.Writer.TryComplete();
            try { await writer; } catch { }
        }
    }

    private void Handle(Conn c, JsonElement m)
    {
        var op = m.TryGetProperty("op", out var o) ? o.GetString() : null;
        var topicName = m.TryGetProperty("topic", out var t) ? t.GetString() : null;
        if (!Enum.TryParse<WsTopic>(topicName, true, out var topic)) return;
        var bot = m.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (topic == WsTopic.Game)
        {
            if (bot is null || _sup.Get(bot) is not { } rt) { c.Out.Writer.TryWrite("{\"topic\":\"error\",\"data\":\"game needs a known bot\"}"); return; }
            lock (c.Games)
            {
                foreach (var (g, v) in c.Games.Where(x => x.Stream.Bot == bot).ToList()) { g.Remove(v); c.Games.Remove((g, v)); }
                if (op == "sub")
                {
                    var stream = _games.GetOrAdd(bot, id => new GameStream(id, rt.GameTap));
                    var viewer = new GameViewer(s => c.Out.Writer.TryWrite(s));
                    stream.Add(viewer);
                    c.Games.Add((stream, viewer));
                }
            }
            return;
        }
        if (op == "sub")
        {
            var hz = m.TryGetProperty("hz", out var h) && h.TryGetInt32(out var hv) ? hv : 5;
            var lv = m.TryGetProperty("level", out var l) ? Http.ParseLevel(l.GetString()) ?? LogLevel.Note : LogLevel.Note;
            var sub = new WsSubscription(topic, bot, hz, lv);
            lock (c.Subs) c.Subs.Add(sub);
            if (bot is not null && topic is WsTopic.Debug or WsTopic.Sensors && _sup.Get(bot) is { } br) Interlocked.Increment(ref br.Watchers);
            if (topic == WsTopic.Fleet) SendState(c, sub, JsonSerializer.SerializeToNode(_sup.Bots.Select(Http.Summary), J)!);
            if (topic == WsTopic.Debug && bot is not null && _sup.Get(bot)?.Frame is { } f) SendState(c, sub, JsonSerializer.SerializeToNode(f, J)!);
            if (topic == WsTopic.Sensors && bot is not null && _sup.Get(bot)?.Sensors is { } sf) SendState(c, sub, JsonSerializer.SerializeToNode(sf, J)!);
        }
        else if (op == "unsub")
        {
            lock (c.Subs)
                foreach (var s in c.Subs.Where(s => s.Topic == topic && (bot is null || s.Bot == bot)).ToList())
                {
                    c.Subs.Remove(s);
                    if (s.Bot is not null && s.Topic is WsTopic.Debug or WsTopic.Sensors && _sup.Get(s.Bot) is { } br) Interlocked.Decrement(ref br.Watchers);
                }
        }
    }
}

/// <summary>RFC 6902 diff of two JSON trees (objects recursively; arrays element-wise when the length is unchanged,
/// replaced otherwise).</summary>
public static class JsonPatch
{
    private static string Esc(string k) => k.Replace("~", "~0").Replace("/", "~1");

    /// <summary>Append the operations that turn a into b.</summary>
    public static void Diff(JsonNode? a, JsonNode? b, string path, JsonArray ops)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var kv in oa) if (!ob.ContainsKey(kv.Key)) ops.Add(new JsonObject { ["op"] = "remove", ["path"] = path + "/" + Esc(kv.Key) });
            foreach (var kv in ob)
            {
                var p = path + "/" + Esc(kv.Key);
                if (!oa.TryGetPropertyValue(kv.Key, out var av)) ops.Add(new JsonObject { ["op"] = "add", ["path"] = p, ["value"] = kv.Value?.DeepClone() });
                else Diff(av, kv.Value, p, ops);
            }
            return;
        }
        if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
        {
            for (var i = 0; i < aa.Count; i++) Diff(aa[i], ab[i], path + "/" + i, ops);
            return;
        }
        if (JsonNode.DeepEquals(a, b)) return;
        ops.Add(new JsonObject { ["op"] = "replace", ["path"] = path, ["value"] = b?.DeepClone() });
    }
}

/// <summary>Maps the WebSocket and the static UI.</summary>
public static class HostMapping
{
    /// <summary>Map /api/v3/ws (and /ws).</summary>
    public static IEndpointRouteBuilder MapWebSocketHub(this IEndpointRouteBuilder app)
    {
        async Task Handler(HttpContext ctx, WebSocketHub hub)
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.ServeAsync(ws, ctx.RequestAborted);
        }
        app.Map("/api/v3/ws", Handler);
        app.Map("/ws", Handler);
        return app;
    }

    /// <summary>Serve the built UI (UI_DIR, default next/ui/dist) with an SPA fallback.</summary>
    public static IEndpointRouteBuilder MapUi(this WebApplication app)
    {
        var dir = Environment.GetEnvironmentVariable("UI_DIR");
        if (dir is null)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fiesta.Bot.Next.slnx"))) d = d.Parent;
            dir = d is null ? "ui/dist" : Path.Combine(d.FullName, "ui", "dist");
        }
        if (!Directory.Exists(dir))
        {
            app.MapGet("/", () => Results.Text($"fiesta bot host: API at /api/v3 (the UI is not built: expected {dir})"));
            return app;
        }
        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(dir));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapFallback(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { type = "about:blank", title = $"no route {ctx.Request.Method} {ctx.Request.Path}", status = 404 }); return; }
            ctx.Response.ContentType = "text/html";
            await ctx.Response.SendFileAsync(Path.Combine(dir, "index.html"));
        });
        return app;
    }
}
