using System.Text.Json;
using System.Threading.Channels;
using Fiesta.Bot.Manager;
using Fiesta.Bot.Scripting;

namespace Fiesta.Bot.Host;

/// <summary>HTTP surface for behaviour scripting: a library of uploaded Lua scripts ( /api/scripts ) and per-bot apply / s…</summary>
public static class ScriptEndpoints
{
    public static void MapScriptEndpoints(this WebApplication app, BotManager? manager,
        ScriptStore store, string? unavailableReason)
    {
        // ── Library (works even without the bot manager — it's just storage) ──────
        var lib = app.MapGroup("/api/scripts").WithTags("Scripts");

        lib.MapGet("/", () => Results.Ok(store.List().Select(s => new
        {
            s.Name, s.UpdatedUtc, chars = s.Source.Length
        })))
        .WithSummary("List uploaded behaviour scripts (name + size + updated)");

        lib.MapGet("/{name}", (string name) =>
        {
            var s = store.Get(name);
            return s is null ? Results.NotFound() : Results.Ok(new { s.Name, s.UpdatedUtc, s.Source });
        })
        .WithSummary("Get an uploaded script's source");

        lib.MapPost("/", (UploadScriptRequest req) =>
        {
            var (ok, error) = store.Upsert(req.Name ?? "", req.Source ?? "");
            return ok
                ? Results.Created($"/api/scripts/{req.Name}", new { name = req.Name, stored = true })
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = [error!] });
        })
        .WithSummary("Upload (or replace) a behaviour script — compile-checked, 400 on a Lua syntax error");

        lib.MapDelete("/{name}", (string name) =>
            store.Delete(name) ? Results.Ok(new { name, deleted = true }) : Results.NotFound())
        .WithSummary("Delete an uploaded script");

        // ── Per-bot apply / stop / status ─────────────────────────────────────────
        var bots = app.MapGroup("/api/bots").WithTags("Scripts");

        if (manager is null)
        {
            IResult Unavailable() => Results.Problem(
                title: "Bot manager unavailable",
                detail: unavailableReason ?? "The bot manager is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
            bots.MapPost("/{id}/script", (string id) => Unavailable()).WithSummary("Apply a script (unavailable)");
            bots.MapPost("/{id}/statemachine", (string id) => Unavailable()).WithSummary("Apply a state machine (unavailable)");
            bots.MapPost("/{id}/script/stop", (string id) => Unavailable()).WithSummary("Stop a script (unavailable)");
            bots.MapGet("/{id}/script", (string id) => Unavailable()).WithSummary("Script status (unavailable)");
            bots.MapGet("/{id}/logstream", (string id) => Unavailable()).WithSummary("Live log stream (unavailable)");
            bots.MapPost("/{id}/graph", (string id) => Unavailable()).WithSummary("Apply a behaviour graph (unavailable)");
            bots.MapPost("/{id}/graph/stop", (string id) => Unavailable()).WithSummary("Stop a behaviour graph (unavailable)");
            bots.MapPost("/{id}/state", (string id) => Unavailable()).WithSummary("Request a state transition (unavailable)");
            bots.MapGet("/{id}/graph", (string id) => Unavailable()).WithSummary("Graph status (unavailable)");
            app.MapGet("/api/graphs", () => Unavailable()).WithTags("Graphs").WithSummary("List graphs (unavailable)");
            return;
        }

        // Behaviour-graph library (states + transitions + scripts, disk-persisted) ──
        var graphs = app.MapGroup("/api/graphs").WithTags("Graphs");
        graphs.MapGet("/", () => Results.Ok(manager.Graphs.List()))
            .WithSummary("List saved behaviour graphs");
        graphs.MapGet("/{name}", (string name) =>
            manager.Graphs.Load(name) is { } g ? Results.Ok(g) : Results.NotFound())
            .WithSummary("Get a behaviour graph (states + transitions + scripts)");
        graphs.MapPost("/", (BehaviorGraph g) =>
        {
            if (string.IsNullOrWhiteSpace(g.Name) || g.States is null || g.States.Count == 0 || string.IsNullOrWhiteSpace(g.Initial))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["graph"] = ["name, initial, and at least one state are required"] });
            manager.Graphs.Save(g);
            return Results.Created($"/api/graphs/{g.Name}", new { g.Name, stored = true, states = g.States.Count, transitions = g.Transitions?.Count ?? 0 });
        })
        .WithSummary("Save (or replace) a behaviour graph: { name, initial, states:[{name,script}], transitions:[{name,from,to,check}], shared? }");
        graphs.MapDelete("/{name}", (string name) =>
            manager.Graphs.Delete(name) ? Results.Ok(new { name, deleted = true }) : Results.NotFound())
            .WithSummary("Delete a behaviour graph");

        bots.MapPost("/{id}/graph", (string id, ApplyGraphRequest req) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            var g = manager.Graphs.Load(req.Name ?? "");
            if (g is null) return Results.NotFound();
            var start = req.StartState ?? manager.Graphs.LoadState(g.Name, id); // resume persisted state if any
            var runner = manager.ApplyGraph(id, g, start, req.TickMs ?? 250);
            return runner is null ? Results.NotFound()
                : Results.Ok(new { id, graph = g.Name, state = runner.CurrentState, status = runner.Status() });
        })
        .WithSummary("Apply a saved behaviour graph to a bot and run it (resumes the persisted state unless startState is given; replaces any running script/graph)");

        bots.MapPost("/{id}/graph/stop", (string id, string? name) =>
            manager.Get(id) is null ? Results.NotFound() : Results.Ok(new { id, stopped = manager.StopGraph(id, name) }))
        .WithSummary("Stop a bot's behaviour graph by ?name=, or all graphs if omitted");

        bots.MapPost("/{id}/state", (string id, StateRequest req) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["target state name is required"] });
            return Results.Ok(new { id, graph = req.Graph, requested = req.Name, ok = manager.RequestState(id, req.Name!, req.Graph) });
        })
        .WithSummary("Request a graph transition to {name} in graph {graph} (graph optional if only one runs); operator flip, e.g. -> stay_alive");

        bots.MapGet("/{id}/graph", (string id) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            var graphs = manager.GraphStatus(id);
            return Results.Ok(new { id, running = graphs.Count > 0, graphs });
        })
        .WithSummary("List a bot's running behaviour graphs (each: current state, ticks, states, last error)");

        bots.MapPost("/{id}/script", (string id, ApplyScriptRequest req) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            var (name, source, err) = Resolve(store, req);
            if (err is not null) return err;
            var runner = manager.ApplyScript(id, name!, source!, req.TickMs ?? 250, req.Trace ?? false);
            return runner is null
                ? Results.NotFound()
                : Results.Ok(new { id, applied = name, trace = req.Trace ?? false, status = runner.Status() });
        })
        .WithSummary("Apply a behaviour script to a bot and loop it (by stored name or inline source; replaces any running script; trace=true logs every bot.* call)");

        bots.MapPost("/{id}/statemachine", (string id, ApplyScriptRequest req) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            var (name, source, err) = Resolve(store, req);
            if (err is not null) return err;
            var runner = manager.ApplyScript(id, name!, source!, req.TickMs ?? 250, req.Trace ?? false);
            return runner is null
                ? Results.NotFound()
                : Results.Ok(new { id, stateMachine = name, status = runner.Status() });
        })
        .WithSummary("Apply a state-machine behaviour to a bot (a script that calls statemachine(states, initial); same runtime as /script, with a current-state in the status)");

        bots.MapPost("/{id}/script/stop", (string id) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            return Results.Ok(new { id, stopped = manager.StopScript(id) });
        })
        .WithSummary("Stop a bot's looping behaviour script");

        bots.MapGet("/{id}/script", (string id) =>
        {
            if (manager.Get(id) is null) return Results.NotFound();
            var st = manager.ScriptStatus(id);
            return st is null
                ? Results.Ok(new { id, running = false })
                : Results.Ok(new { id, running = true, script = st });
        })
        .WithSummary("Debug a bot's running script (state, ticks, events handled, last error, globals)");

        bots.MapGet("/{id}/logstream", async (string id, HttpContext ctx, int? tail) =>
        {
            var bot = manager.Get(id);
            if (bot is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

            ctx.Response.ContentType = "application/x-ndjson";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

            // Bounded so a slow/abandoned reader can't grow memory: drop oldest on overflow
            var ch = Channel.CreateBounded<string>(
                new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
            void OnLine(string l) => ch.Writer.TryWrite(l);

            // Backfill the recent buffer first (so a fresh connection has context), then subscribe for live lines
            foreach (var l in bot.RecentLines(tail ?? 50)) ch.Writer.TryWrite(l);
            bot.LogLine += OnLine;
            var ct = ctx.RequestAborted;
            try
            {
                await foreach (var line in ch.Reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { line }) + "\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally { bot.LogLine -= OnLine; }
        })
        .WithSummary("Live-tail a bot's log as NDJSON (script + engine lines; ?tail=N backfills the last N). curl -N to watch Lua run.");
    }

    /// <summary>Resolve an apply request to (name, source): a stored library Name , or inline Source (compile-checked, labelle…</summary>
    private static (string? Name, string? Source, IResult? Error) Resolve(ScriptStore store, ApplyScriptRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            var stored = store.Get(req.Name!);
            return stored is null
                ? (null, null, Results.NotFound(new { error = $"no stored script '{req.Name}'" }))
                : (stored.Name, stored.Source, null);
        }
        if (!string.IsNullOrEmpty(req.Source))
        {
            return ScriptStore.Compile(req.Source!) is { } err
                ? (null, null, Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = [err] }))
                : (req.NameAs ?? "inline", req.Source, null);
        }
        return (null, null, Results.ValidationProblem(
            new Dictionary<string, string[]> { ["name/source"] = ["give a stored 'name' or inline 'source'"] }));
    }
}

/// <summary>Body for POST /api/scripts — upload/replace a library script</summary>
public sealed record UploadScriptRequest
{
    public string? Name { get; init; }
    public string? Source { get; init; }
}

public sealed record ApplyScriptRequest
{
    public string? Name { get; init; }
    public string? Source { get; init; }
    public string? NameAs { get; init; }
    public int? TickMs { get; init; }

    /// <summary>Log every bot.* call (with args) — tail it via GET /api/bots/{id}/logstream</summary>
    public bool? Trace { get; init; }
}

/// <summary>Body for POST /api/bots/{id}/graph — run a saved graph by Name</summary>
public sealed record ApplyGraphRequest
{
    public string? Name { get; init; }
    public string? StartState { get; init; }
    public int? TickMs { get; init; }
}

/// <summary>Body for POST /api/bots/{id}/state — request a transition to Name in graph Graph (Graph optional when only one…</summary>
public sealed record StateRequest
{
    public string? Graph { get; init; }
    public string? Name { get; init; }
}
