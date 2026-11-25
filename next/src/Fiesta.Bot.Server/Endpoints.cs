using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;
using Fiesta.Bot.Brain;
using Fiesta.Bot.Fleet;
using Fiesta.Bot.Protocol;
using Fiesta.Bot.Services;
using Fiesta.Bot.Services.Logging;
using LogLevel = Fiesta.Bot.Services.Logging.LogLevel;
using Microsoft.AspNetCore.Mvc;

namespace Fiesta.Bot.Server;

/// <summary>Spawn request body (API_SPEC 8.1 + UI U6).</summary>
public sealed record SpawnRequest(string Id, string? Host, int? LoginPort, string Username, string? Password, string Character,
    bool Create = false, string? Class = null, string? Brain = null, string? Channel = null, bool Autostart = true, string? Wire = null,
    int World = 0, string? PasswordMd5 = null);

/// <summary>Bot configuration patch (UI U7).</summary>
public sealed record BotPatch(string? Host, int? LoginPort, string? Username, string? Password, string? Character, bool? Autostart, string? Wire, bool? Create, string? Class);

/// <summary>Force-state body.</summary>
public sealed record ForceStateRequest(string Layer, string State);

/// <summary>Blackboard edit body.</summary>
public sealed record BlackboardEdit(string Scope, string Key, JsonElement Value, string? Owner = null);

/// <summary>Operator action body.</summary>
public sealed record OperatorActionRequest(string Kind, Dictionary<string, JsonElement>? Args);

/// <summary>Create-version body.</summary>
public sealed record CreateVersionRequest(string Definition, Dictionary<string, string>? Modules, string? Message, string? Author);

/// <summary>Apply-brain body.</summary>
public sealed record ApplyBrainRequest(string Name, string? Hash, string? Channel, bool KeepState = true, string? Reason = null);

/// <summary>Library version body.</summary>
public sealed record LibraryVersionRequest(string Source, string? Message, string? Author, string? Path);

/// <summary>Log level body.</summary>
public sealed record LogLevelRequest(string Category, string Level, double? ExpiryMinutes);

/// <summary>Packet capture body.</summary>
public sealed record PacketCaptureRequest(bool Enabled, List<int>? Opcodes, List<int>? Depts, double? Minutes, double? MaxMb, string? Format);

/// <summary>Shared helpers for the endpoints.</summary>
public static class Http
{
    /// <summary>RFC 7807 problem.</summary>
    public static IResult Problem(int status, string title, object? lint = null)
        => Results.Json(lint is null ? new { type = "about:blank", title, status } : new { type = "about:blank", title, status, lint }, statusCode: status, contentType: "application/problem+json");

    /// <summary>JSON options matching the host's.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Level letter for a log level.</summary>
    public static string Letter(LogLevel l) => "CNIDT"[(int)l].ToString();

    /// <summary>Parse a level name or letter.</summary>
    public static LogLevel? ParseLevel(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "c" or "crit" => LogLevel.Crit, "n" or "note" => LogLevel.Note, "i" or "info" => LogLevel.Info,
        "d" or "debug" or "verbose" => LogLevel.Debug, "t" or "trace" => LogLevel.Trace, _ => null,
    };

    /// <summary>A log entry as the UI reads it.</summary>
    public static object Line(in LogEntry e) => new { t = SystemClock.ToUtc(e.At), level = Letter(e.Level), category = e.Category.ToString().ToLowerInvariant(), text = LogRenderer.Body(e), repeat = e.Repeat + 1 };

    /// <summary>Summary row of a bot (API_SPEC 8.1 + UI 13.3).</summary>
    public static BotSummary Summary(BotRuntime b)
    {
        var r = b.Record;
        var snap = b.Agent?.Snapshot;
        var f = b.Frame;
        var layerStates = new Dictionary<string, string>();
        var since = new Dictionary<string, DateTime>();
        if (f is not null)
            foreach (var l in f.Layers)
            {
                layerStates[l.Name] = string.Join('/', l.Path);
                if (l.EnteredAtPath.Count > 0) since[l.Name] = l.EnteredAtPath[0];
            }
        var hours = Math.Max(1.0 / 60, (DateTime.UtcNow - b.SessionStart).TotalHours);
        var lt = f?.LastTransitions.FirstOrDefault();
        var paused = snap?.Paused == true || f?.Paused == true;
        var state = paused && b.State == SupervisorState.InZone ? "Paused" : b.State.ToString();   // a stopped bot keeps its last frame
        var stats = b.Agent?.Stats;
        var p99 = stats is null ? 0 : stats.RecentQuantile(x => x.TotalUs, 0.99, 1200);
        string? health = b.State == SupervisorState.Offline ? null
            : b.Brain?.Suspended == true ? "brain suspended (budget)"
            : Cycle(b) is { } cyc ? cyc
            : b.State == SupervisorState.InZone && b.Brain is not null && DateTime.UtcNow - b.LastProgressAt > TimeSpan.FromMinutes(10)
                ? $"no progress: no exp, level or quest change for {(int)(DateTime.UtcNow - b.LastProgressAt).TotalMinutes} min"
            : b.Brain is { } br && br.LoadErrors.Count > 0 ? "brain load errors"
            : p99 > 1000 ? "tick over budget"
            : b.Agent is { LastTickAt: >= 0 } a && SystemClock.Instance.NowMs - a.LastTickAt > 2000 ? "no tick"
            : "ok";
        return new BotSummary(r.Id, state, snap?.Map, snap?.Level ?? 0, layerStates, r.VersionHash, Math.Round(p99, 1),
            snap is null ? 0 : Math.Round(snap.ExpGained / hours), snap is null ? 0 : Math.Round(snap.Deaths / hours, 2), b.LastError, health)
        {
            Character = r.Character, Username = r.Username, Host = r.Host, LoginPort = r.LoginPort, Wire = r.Wire,
            ClassFamily = b.Agent?.Context.World.Self.ClassFamily ?? (r.ClassId is { } c ? c : 0), Brain = r.Brain, Channel = r.Channel,
            Autostart = r.Autostart, PacketLog = r.PacketLog, Paused = paused,
            TicksPerSec = b.Agent is null || paused ? 0 : 1000.0 / b.Agent.IntervalMs,
            LayerSince = since, Version = f?.Version,
            LastTransition = lt is null ? null : new { at = lt.At, layer = lt.Layer, from = lt.From, to = lt.To, reason = lt.Reason },
            Vitals = snap is null ? null : new
            {
                hp = snap.Hp, maxHp = snap.MaxHp, sp = snap.Sp, maxSp = snap.MaxSp, exp = snap.Exp, expNext = snap.NextExp, money = snap.Money,
                bagFree = snap.BagFree, bagSize = b.Agent?.Context.World.Bag.Size ?? 0, x = Math.Round(snap.X), y = Math.Round(snap.Y), runSpeed = Math.Round(snap.RunSpeed, 1),
            },
        };
    }

    /// <summary>REDESIGN 10: a layer flipping between two states more than 20 times in a minute.</summary>
    public static string? Cycle(BotRuntime b)
    {
        if (b.Brain is not { } br) return null;
        var since = DateTime.UtcNow.AddMinutes(-1);
        var counts = new Dictionary<(string, string, string), int>();
        foreach (var t in br.Debug.Transitions())
        {
            if (t.At < since || t.Kind == "operator") continue;
            var a = string.CompareOrdinal(t.From, t.To) < 0 ? (t.Layer, t.From, t.To) : (t.Layer, t.To, t.From);
            counts[a] = counts.GetValueOrDefault(a) + 1;
        }
        foreach (var (k, n) in counts) if (n > 40) return $"transition cycle: {k.Item1} {k.Item2} <-> {k.Item3} x{n / 2} in 60 s";
        return null;
    }

    /// <summary>The detail view (summary + record without password + levels + capture).</summary>
    public static JsonNode Detail(BotRuntime b)
    {
        var node = JsonSerializer.SerializeToNode(Summary(b), Json)!.AsObject();
        var rec = JsonSerializer.SerializeToNode(b.Record, Json)!.AsObject();
        rec.Remove("passwordMd5");
        node["record"] = rec;
        node["logLevels"] = JsonSerializer.SerializeToNode(LogLevels(b), Json);
        node["packetCapture"] = JsonSerializer.SerializeToNode(Capture(b), Json);
        return node;
    }

    /// <summary>Log levels as the UI reads them.</summary>
    public static Dictionary<string, object> LogLevels(BotRuntime b)
        => b.Log.Levels.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => (object)new { level = kv.Value.Level.ToString().ToLowerInvariant(), expiresAt = kv.Value.ExpiresAt });

    /// <summary>Packet capture state.</summary>
    public static object Capture(BotRuntime b)
    {
        var s = b.Capture.Settings;
        var f = new FileInfo(b.Capture.FilePath);
        return new
        {
            enabled = s.Enabled, opcodes = s.Opcodes ?? [], depts = s.Depts ?? [], minutes = s.Duration.TotalMinutes, maxMb = s.MaxBytes / (1024.0 * 1024),
            format = s.Binary ? "binary" : "text",
            files = f.Exists ? new[] { new { name = f.Name, sizeBytes = f.Length, startedAt = f.CreationTimeUtc } } : [],
        };
    }

    /// <summary>Read a JsonElement arg as a string.</summary>
    public static string Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
}

/// <summary>Fleet control endpoints (API_SPEC 8.1, UI 13.2-13.3).</summary>
public static class FleetEndpoints
{
    /// <summary>Map /api/v3/host and /api/v3/bots/*.</summary>
    public static RouteGroupBuilder MapFleetEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/host", (HostServices h) =>
        {
            var classes = Enumerable.Range(1, 63).Select(i => new { id = i, name = h.Data.ClassName(i) }).Where(c => c.name.Length > 0 && h.Data.ClassFamily(c.id) == c.id).ToList();
            return Results.Json(new
            {
                name = Environment.MachineName, apiVersion = ApiManifest.Version, wires = new[] { "2016", "2026" },
                defaultWire = WireProfile.For(h.DefaultWire).Name, defaultLogin = new { host = h.DefaultLogin.Host, loginPort = h.DefaultLogin.Port },
                classes, channels = new[] { "stable", "candidate", "operator-test" }, mock = false,
                clientData = h.RessystemDir, tickWorkers = h.Ticks.Load.Count,
            }, Http.Json);
        });
        api.MapGet("/bots", (BotSupervisor s) => Results.Json(s.Bots.Select(Http.Summary), Http.Json));
        api.MapPost("/bots", (SpawnRequest q, BotSupervisor s, HostServices h) =>
        {
            if (string.IsNullOrWhiteSpace(q.Id) || q.Id.Length is < 2 or > 32 || !q.Id.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                return Http.Problem(400, "id: 2-32 letters, digits, _ or -");
            if (s.Get(q.Id) is not null) return Http.Problem(409, $"bot {q.Id} exists");
            if (string.IsNullOrWhiteSpace(q.Username) || string.IsNullOrWhiteSpace(q.Character)) return Http.Problem(400, "username and character are required");
            if (q.Wire is { } w && w is not ("2016" or "2026")) return Http.Problem(400, "wire must be 2016 or 2026");
            int? cls = q.Class is null ? null : int.TryParse(q.Class, out var ci) ? ci : Enumerable.Range(1, 63).FirstOrDefault(i => string.Equals(h.Data.ClassName(i), q.Class, StringComparison.OrdinalIgnoreCase)) is var f and > 0 ? f : null;
            var rec = new BotRecord(q.Id, q.Username, q.PasswordMd5 ?? Fiesta.Bot.Protocol.Ascii.Md5Hex(q.Password ?? ""), q.Character, q.Host ?? h.DefaultLogin.Host,
                q.LoginPort ?? h.DefaultLogin.Port, q.Brain, q.Channel, null, q.Autostart, false)
            { Wire = q.Wire ?? WireProfile.For(h.DefaultWire).Name, Create = q.Create, ClassId = cls, World = q.World };
            var b = s.Spawn(rec, q.Autostart);
            return Results.Json(Http.Summary(b), Http.Json, statusCode: 201);
        });
        api.MapGet("/bots/{id}", (string id, BotSupervisor s) => s.Get(id) is { } b ? Results.Json(Http.Detail(b), Http.Json) : Http.Problem(404, $"no bot {id}"));
        api.MapPatch("/bots/{id}", (string id, BotPatch p, BotSupervisor s, HostServices h) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            if (p.Wire is { } w && w is not ("2016" or "2026")) return Http.Problem(400, "wire must be 2016 or 2026");
            var r = b.Record;
            r = r with
            {
                Host = p.Host ?? r.Host, LoginPort = p.LoginPort ?? r.LoginPort, Username = p.Username ?? r.Username,
                PasswordMd5 = p.Password is { } pw ? Fiesta.Bot.Protocol.Ascii.Md5Hex(pw) : r.PasswordMd5, Character = p.Character ?? r.Character,
                Autostart = p.Autostart ?? r.Autostart, Wire = p.Wire ?? r.Wire, Create = p.Create ?? r.Create,
                ClassId = p.Class is { } c && int.TryParse(c, out var ci) ? ci : r.ClassId,
            };
            s.Update(b, r);
            b.Log.Text(LogCategory.Core, LogLevel.Note, $"config changed (applies at the next login){(p.Wire is null ? "" : $": wire {p.Wire}")}");
            return Results.Json(Http.Summary(b), Http.Json);
        });
        api.MapDelete("/bots/{id}", async (string id, BotSupervisor s) => { if (s.Get(id) is null) return Http.Problem(404, $"no bot {id}"); await s.ForgetAsync(id); return Results.NoContent(); });
        api.MapPost("/bots/{id}/start", (string id, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            b.Start();
            return Results.Json(Http.Summary(b), Http.Json);
        });
        api.MapPost("/bots/{id}/stop", async (string id, BotSupervisor s, HttpRequest req) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var logout = true;
            if (req.ContentLength > 0) { var doc = await JsonDocument.ParseAsync(req.Body); if (doc.RootElement.TryGetProperty("logout", out var l)) logout = l.GetBoolean(); }
            await b.StopAsync(logout);
            return Results.Json(Http.Summary(b), Http.Json);
        });
        api.MapPost("/bots/{id}/relog", async (string id, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            if (b.Agent is null) return Http.Problem(409, $"bot {id} is offline");
            await b.RelogAsync();
            return Results.Json(Http.Summary(b), Http.Json);
        });
        api.MapPost("/bots/{id}/pause", async (string id, BotSupervisor s) => await Command(s, id, new ControlCommand(ControlKind.Pause), b => Http.Summary(b)));
        api.MapPost("/bots/{id}/resume", async (string id, BotSupervisor s) => await Command(s, id, new ControlCommand(ControlKind.Resume), b => Http.Summary(b)));
        api.MapPost("/bots/{id}/step", async (string id, BotSupervisor s, HttpRequest req) =>
        {
            var n = 1;
            if (req.ContentLength > 0) { var doc = await JsonDocument.ParseAsync(req.Body); if (doc.RootElement.TryGetProperty("ticks", out var t)) n = Math.Clamp(t.GetInt32(), 1, 200); }
            if (s.Get(id) is { Agent.Paused: false }) return Http.Problem(409, "pause the brain before stepping");
            return await Command(s, id, new ControlCommand(ControlKind.Step, Count: n), _ => new { queued = n });
        });
        api.MapPost("/bots/{id}/force-state", async (string id, ForceStateRequest q, BotSupervisor s) =>
            await Command(s, id, new ControlCommand(ControlKind.ForceState, Layer: q.Layer, State: q.State), null));
        api.MapPut("/bots/{id}/blackboard", async (string id, BlackboardEdit q, BotSupervisor s) =>
        {
            if (q.Scope is not ("bot" or "layer" or "state")) return Http.Problem(400, "scope: bot, layer or state");
            if (q.Scope != "bot" && q.Owner is null) return Http.Problem(400, $"owner (layer) required for {q.Scope} scope");
            return await Command(s, id, new ControlCommand(ControlKind.SetBlackboard, Layer: q.Owner, Key: q.Key, Value: q.Value.GetRawText()) { Scope = q.Scope }, null);
        });
        api.MapPost("/bots/{id}/actions", async (string id, OperatorActionRequest q, BotSupervisor s) =>
        {
            var kind = q.Kind.Replace("_", "").ToLowerInvariant();
            var args = (q.Args ?? new()).ToDictionary(kv => kv.Key, kv => Http.Str(kv.Value));
            if (s.Get(id) is not { Agent: not null } b) return Http.Problem(409, $"bot {id} is offline");
            var r = await b.CommandAsync(new ControlCommand(ControlKind.Action, Value: kind, By: "operator") { Args = args });
            if (r is string err) return Http.Problem(400, err);
            var node = JsonSerializer.SerializeToNode(r, Http.Json)!.AsObject();
            var aid = node["action"]?.GetValue<int>() ?? -1;
            var state = aid < 0 ? "rejected" : (await b.QueryAsync(c => c.Actions.Get(new ActionId(aid)).State.ToString().ToLowerInvariant()))?.ToString();
            return Results.Json(new { id = aid, state, rejected = node["rejected"]?.ToString() }, Http.Json);
        });
        api.MapPost("/bots/{id}/packetlog", async (string id, BotSupervisor s, HttpRequest req) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var doc = await JsonDocument.ParseAsync(req.Body);
            var on = doc.RootElement.TryGetProperty("enabled", out var e) && e.GetBoolean();
            b.Capture.Configure(PacketCaptureSettings.Off with { Enabled = on, Duration = TimeSpan.FromDays(3650) });
            s.Update(b, b.Record with { PacketLog = on });
            return Results.Json(new { enabled = on, file = b.Capture.FilePath }, Http.Json);
        });
        return api;
    }

    internal static async Task<IResult> Command(BotSupervisor s, string id, ControlCommand cmd, Func<BotRuntime, object>? shape)
    {
        if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
        if (b.Agent is null) return Http.Problem(409, $"bot {id} is offline");
        try
        {
            var r = await b.CommandAsync(cmd);
            await Task.Delay(60);   // let the next publish carry the change
            return Results.Json(shape is null ? r : shape(b), Http.Json);
        }
        catch (ArgumentException e) { return Http.Problem(400, e.Message); }
        catch (TimeoutException) { return Http.Problem(504, "the bot's tick did not answer"); }
    }
}

/// <summary>Brain/library/version endpoints (API_SPEC 8.2, UI 13.5-13.6).</summary>
public static class BrainEndpoints
{
    private static object Info(StoredVersion v) => new
    {
        hash = v.Hash, brain = v.Brain, api = v.Api, created = v.Created, author = v.Author, message = v.Message,
        modules = v.Modules.Keys.Concat(v.Libraries.Keys).OrderBy(x => x).ToList(), libraries = v.Libraries,
        warnings = v.Lint.Count(l => l.Severity != LintSeverity.Error),
    };

    private static object Lint(LintResult l) => new { severity = l.Severity.ToString().ToLowerInvariant(), code = l.Code, message = l.Message, module = l.Module, line = l.Line, column = (int?)null, layer = l.Layer, state = l.State };

    /// <summary>Map the routes.</summary>
    public static RouteGroupBuilder MapBrainEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/brains", (HostServices h) => Results.Json(h.Brains.Brains().Select(n =>
        {
            var vs = h.Brains.Versions(n);
            string? desc = null;
            try { desc = vs.Count > 0 ? BrainLoader.Parse(vs[^1].Definition).Description : null; } catch (BrainFormatException) { }
            return new { name = n, latest = vs.LastOrDefault()?.Hash, channels = h.Brains.Channels(n), description = desc ?? "", versions = vs.Count };
        }), Http.Json));
        api.MapGet("/brains/{name}/versions", (string name, HostServices h) => Results.Json(h.Brains.Versions(name).Reverse().Select(Info), Http.Json));
        api.MapGet("/brains/{name}/versions/{hash}", (string name, string hash, HostServices h) =>
        {
            if (h.Brains.Find(name, hash) is not { } v) return Http.Problem(404, "no such version");
            var libs = v.Libraries.ToDictionary(kv => kv.Key, kv => (object)new { hash = kv.Value, source = h.Brains.Library(kv.Key, kv.Value)?.Source ?? "" });
            var info = JsonSerializer.SerializeToNode(Info(v), Http.Json)!.AsObject();
            info["definition"] = v.Definition;
            info["modules"] = JsonSerializer.SerializeToNode(v.Modules, Http.Json);
            info["libraries"] = JsonSerializer.SerializeToNode(libs, Http.Json);
            info["lint"] = JsonSerializer.SerializeToNode(v.Lint.Select(Lint), Http.Json);
            return Results.Json(info, Http.Json);
        });
        api.MapPost("/brains/{name}/versions", (string name, CreateVersionRequest q, HostServices h) =>
        {
            if (name.Length == 0 || !char.IsLower(name[0]) || !name.All(c => char.IsLower(c) || char.IsDigit(c) || c is '_' or '-'))
                return Http.Problem(400, "brain name must match ^[a-z][a-z0-9_-]*$");
            var (v, lint) = h.Brains.Create(name, q.Definition, q.Modules ?? new(), q.Author ?? "operator", q.Message ?? "");
            return v is null ? Http.Problem(400, "lint errors", lint.Select(Lint)) : Results.Json(Info(v), Http.Json, statusCode: 201);
        });
        api.MapPost("/brains/lint", (CreateVersionRequest q, HostServices h) => Results.Json(h.Brains.Lint(q.Definition, q.Modules ?? new()).Lint.Select(Lint), Http.Json));
        api.MapGet("/brains/{name}/diff", (string name, string from, string to, HostServices h) =>
        {
            if (h.Brains.Find(name, from) is not { } a || h.Brains.Find(name, to) is not { } b) return Http.Problem(404, "from/to version not found");
            var sa = h.Brains.Sources(a); var sb = h.Brains.Sources(b);
            var paths = sa.Keys.Union(sb.Keys).OrderBy(x => x).ToList();
            return Results.Json(new
            {
                from = a.Hash, to = b.Hash,
                definitionDiff = TextDiff.Unified(a.Definition, b.Definition, $"brain.yaml@{a.Hash}", $"brain.yaml@{b.Hash}"),
                modules = paths.Select(p => new
                {
                    path = p,
                    unifiedDiff = TextDiff.Unified(sa.GetValueOrDefault(p) ?? "", sb.GetValueOrDefault(p) ?? "", $"{p}@{a.Hash}", $"{p}@{b.Hash}"),
                    status = !sa.ContainsKey(p) ? "added" : !sb.ContainsKey(p) ? "removed" : sa[p] == sb[p] ? "same" : "changed",
                }),
            }, Http.Json);
        });
        api.MapPut("/brains/{name}/channels/{channel}", async (string name, string channel, HttpRequest req, HostServices h, BotSupervisor s) =>
        {
            var doc = await JsonDocument.ParseAsync(req.Body);
            var hash = doc.RootElement.GetProperty("hash").GetString() ?? "";
            if (h.Brains.Find(name, hash) is not { } v) return Http.Problem(404, "no such version");
            h.Brains.SetChannel(name, channel, v.Hash);
            foreach (var b in s.Bots.Where(b => b.Record.Brain == name && b.Record.Channel == channel))
                await b.ApplyAsync(name, v.Hash, channel, true, "operator", $"channel {channel} moved");
            return Results.Json(new { brain = name, channel, hash = v.Hash }, Http.Json);
        });
        api.MapGet("/brains/{name}/assignments", (string name, HostServices h) => Results.Json(h.Brains.Assignments(brain: name), Http.Json));
        api.MapGet("/brains/{name}/metrics", (string name, string? versions, HostServices h, BotSupervisor s) =>
        {
            var want = (versions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var list = h.Brains.Versions(name).Where(v => want.Length == 0 || want.Any(w => v.Hash.StartsWith(w))).ToList();
            return Results.Json(list.Select(v => new
            {
                hash = v.Hash,
                perClass = s.Bots.Where(b => b.Record.Brain == name && b.Record.VersionHash == v.Hash && b.Agent is not null)
                    .GroupBy(b => b.Agent!.Context.World.Self.ClassFamily).Select(g =>
                    {
                        var hours = g.Sum(b => Math.Max(1.0 / 60, (DateTime.UtcNow - b.SessionStart).TotalHours));
                        var snaps = g.Select(b => b.Agent!.Snapshot).ToList();
                        return new
                        {
                            classFamily = g.Key, bots = g.Count(), hours = Math.Round(hours, 2), expPerHour = Math.Round(snaps.Sum(x => x.ExpGained) / hours),
                            deathsPerHour = Math.Round(snaps.Sum(x => x.Deaths) / hours, 2), questsPerHour = 0.0, stuckExitsPerHour = 0.0,
                            tickP99Us = g.Max(b => b.Agent!.Stats.RecentQuantile(x => x.TotalUs, 0.99)), stateShare = new Dictionary<string, double>(),
                        };
                    }).ToList(),
            }), Http.Json);
        });
        api.MapPost("/bots/{id}/brain", async (string id, ApplyBrainRequest q, BotSupervisor s, HostServices h) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var hash = q.Hash ?? (q.Channel is { } c ? h.Brains.ResolveChannel(q.Name, c) : h.Brains.Versions(q.Name).LastOrDefault()?.Hash);
            if (hash is null || h.Brains.Find(q.Name, hash) is not { } v) return Http.Problem(404, $"no version for brain {q.Name}");
            try { return Results.Json(await b.ApplyAsync(q.Name, v.Hash, q.Hash is null ? q.Channel : null, q.KeepState, "operator", q.Reason ?? (q.Channel is null ? "apply" : $"channel {q.Channel}")), Http.Json); }
            catch (Exception e) { return Http.Problem(400, e.Message); }
        });
        api.MapPost("/bots/{id}/brain/rollback", async (string id, HttpRequest req, BotSupervisor s, HostServices h) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            string? to = null;
            if (req.ContentLength > 0) { var doc = await JsonDocument.ParseAsync(req.Body); if (doc.RootElement.TryGetProperty("to", out var t)) to = t.GetString(); }
            to ??= h.Brains.Assignments(bot: id).FirstOrDefault()?.FromHash;
            if (to is null || b.Record.Brain is null) return Http.Problem(409, "nothing to roll back to");
            return Results.Json(await b.ApplyAsync(b.Record.Brain, to, null, true, "operator", "rollback"), Http.Json);
        });
        api.MapGet("/bots/{id}/assignments", (string id, HostServices h) => Results.Json(h.Brains.Assignments(bot: id), Http.Json));
        api.MapGet("/libraries", (HostServices h) => Results.Json(h.Brains.Libraries().Select(p =>
        {
            var vs = h.Brains.LibraryVersions(p);
            var users = h.Brains.Brains().Where(n => h.Brains.Versions(n).LastOrDefault() is { } v && (v.Libraries.ContainsKey(p) || v.Definition.Contains(p))).ToList();
            return new { path = p, latest = vs.LastOrDefault()?.Hash, readOnly = p.StartsWith("lib/"), versions = vs.Count, usedBy = users };
        }), Http.Json));
        api.MapGet("/libraries/{*rest}", (string rest, HostServices h) =>
        {
            var (path, hash) = SplitLib(rest);
            if (hash is null) return Results.Json(h.Brains.LibraryVersions(path).Reverse().Select(v => new { v.Hash, v.Path, v.Created, v.Author, v.Message }), Http.Json);
            return h.Brains.Library(path, hash) is { } lv ? Results.Json(lv, Http.Json) : Http.Problem(404, "no such library version");
        });
        api.MapPost("/libraries/lint", (LibraryVersionRequest q) => Results.Json(LintLibrary(q.Path ?? "user/x/y", q.Source).Select(Lint), Http.Json));
        api.MapPost("/libraries/{*rest}", (string rest, LibraryVersionRequest q, HostServices h) =>
        {
            var (path, _) = SplitLib(rest);
            if (path.StartsWith("lib/")) return Http.Problem(403, "lib/* is host-provided and read-only");
            if (!(path.StartsWith("user/") || path.StartsWith("brain/")) || path.Count(c => c == '/') < 2) return Http.Problem(400, "path must be user/<owner>/<name> or brain/<brain>/<name>");
            var lint = LintLibrary(path, q.Source);
            if (lint.Any(l => l.Severity == LintSeverity.Error)) return Http.Problem(400, "lint errors", lint.Select(Lint));
            var v = h.Brains.CreateLibraryVersion(path, q.Source, q.Author ?? "operator", q.Message ?? "");
            return Results.Json(new { v.Hash, v.Path, v.Created, v.Author, v.Message, lint = lint.Select(Lint) }, Http.Json, statusCode: 201);
        });
        api.MapGet("/api-manifest", (HostServices h) => Results.Text(Manifest.Json(), "application/json"));
        return api;
    }

    private static List<LintResult> LintLibrary(string path, string source)
    {
        var def = new BrainDefinition("lint", ApiManifest.Version, null, [path], new Dictionary<string, object?>(), new Dictionary<string, MachineDef>(),
            [new LayerDef("l", 1, LayerRate.Tick, 2, true, new MachineDef("s", null, new Dictionary<string, StateDef> { ["s"] = new(null, null, null, null, null, Resource.None, 0, null, 0, null, true, null, []) }))]);
        return BrainLinter.Lint(def, new Dictionary<string, string> { [path] = source }).Where(l => l.Module is not null).ToList();
    }

    // "user/ikaron/heal/versions[/hash]" (also URL-encoded as one segment)
    private static (string Path, string? Hash) SplitLib(string rest)
    {
        rest = Uri.UnescapeDataString(rest);
        var i = rest.LastIndexOf("/versions", StringComparison.Ordinal);
        if (i < 0) return (rest, null);
        var path = rest[..i];
        var tail = rest[(i + "/versions".Length)..].Trim('/');
        return (path, tail.Length == 0 ? null : tail);
    }
}

/// <summary>The API manifest in the shape the editor reads (UI 13.5).</summary>
public static class Manifest
{
    private static string? _json;
    private static readonly Dictionary<string, (string Arg1, string Arg2, string Doc)> EventDocs = new()
    {
        ["DAMAGED"] = ("attacker", "damage", "we took damage"), ["HIT"] = ("target", "damage", "we dealt damage"),
        ["TARGET_DIED"] = ("handle", "mobId", "an entity died"), ["KILL"] = ("handle", "mobId", "a mob we damaged died"),
        ["AGGRO_GAINED"] = ("handle", "-", "a mob became aggro on us"), ["AGGRO_LOST"] = ("handle", "-", "it stopped (leash, death, left view)"),
        ["ENTITY_APPEARED"] = ("handle", "kind", ""), ["ENTITY_LEFT"] = ("handle", "kind", ""), ["HP_CHANGED"] = ("value", "max", ""), ["SP_CHANGED"] = ("value", "max", ""),
        ["LEVEL_UP"] = ("level", "-", ""), ["MAP_CHANGED"] = ("mapId", "previous", ""), ["MOVE_FAILED"] = ("kind (0 desync / 1 blocked)", "-", "MOVEFAIL classified"),
        ["ARRIVED"] = ("action", "-", "a move action finished"), ["CAST_RESULT"] = ("skill", "code", ""), ["ACTION_RESOLVED"] = ("action", "state", "any action left the pending state"),
        ["PROCEDURE_DONE"] = ("procedure kind", "ok (0/1)", ""), ["JOB_DONE"] = ("ticket", "ok", ""), ["QUEST_PROGRESS"] = ("quest", "objective", ""),
        ["QUEST_DONE"] = ("quest", "-", ""), ["QUEST_ACCEPTED"] = ("quest", "-", ""), ["ITEM_GAINED"] = ("itemId", "lot", ""), ["ITEM_LOST"] = ("itemId", "lot", ""),
        ["BAG_FULL"] = ("-", "-", ""), ["MONEY_CHANGED"] = ("money", "delta", ""), ["CHAT"] = ("fromHandle", "-", "text via ev.chatText()"), ["WHISPER"] = ("-", "-", ""),
        ["PARTY_INVITE"] = ("fromHandle", "-", ""), ["TRADE_ASKED"] = ("fromHandle", "-", ""), ["DIED"] = ("seconds", "-", ""), ["REVIVED"] = ("mapId", "-", ""),
        ["STUCK"] = ("layer index", "depth", "a state's progress stalled"), ["BUDGET"] = ("layer index", "-", "a state tick hit its budget"), ["OPERATOR"] = ("command", "-", ""),
    };

    /// <summary>The manifest JSON.</summary>
    public static string Json()
    {
        if (_json is not null) return _json;
        var gen = JsonNode.Parse(GeneratedApi.ManifestJson)!;
        var fns = new JsonArray();
        static string Cost(string c) => c switch { "O1" => "O(1)", "Ok" => "O(k)", "On" => "O(n)", "Cached" => "cached", _ => "job" };
        static string Alloc(string a) => a switch { "None" => "0", "LuaString" => "lua-str", "Iterator" => "iter", _ => "small" };
        var resources = new Dictionary<string, string> { ["act"] = "", ["npc"] = "Npc" };
        foreach (var m in gen["modules"]!.AsArray())
        {
            var mn = m!["name"]!.GetValue<string>();
            foreach (var f in m["functions"]!.AsArray())
            {
                var ps = f!["params"]!.AsArray().Select(p => p!["type"]!.GetValue<string>() == "any" ? "..." : p["name"]!.GetValue<string>() + (p["default"] is { } d ? "=" + d : "")).Where(p => p != "tbl" || true).ToList();
                var name = f["name"]!.GetValue<string>();
                fns.Add(new JsonObject
                {
                    ["module"] = mn, ["name"] = name, ["signature"] = $"{mn}.{name}({string.Join(", ", ps)})",
                    ["params"] = new JsonArray(ps.Select(p => (JsonNode?)p).ToArray()), ["returns"] = f["returns"]!.GetValue<string>(),
                    ["cost"] = Cost(f["cost"]!.GetValue<string>()), ["alloc"] = Alloc(f["alloc"]!.GetValue<string>()), ["doc"] = f["doc"]!.GetValue<string>(),
                });
                if (f["alias"] is { } al && al.GetValueKind() == JsonValueKind.String)
                    fns.Add(new JsonObject { ["module"] = "_G", ["name"] = al.GetValue<string>(), ["signature"] = $"{al.GetValue<string>()}({string.Join(", ", ps)})", ["params"] = new JsonArray(ps.Select(p => (JsonNode?)p).ToArray()), ["returns"] = f["returns"]!.GetValue<string>(), ["cost"] = Cost(f["cost"]!.GetValue<string>()), ["alloc"] = "0", ["doc"] = f["doc"]!.GetValue<string>(), ["aliasOf"] = $"{mn}.{name}" });
            }
        }
        fns.Add(new JsonObject { ["module"] = "_G", ["name"] = "log", ["signature"] = "log(fmt, ...)", ["params"] = new JsonArray("fmt", "..."), ["returns"] = "void", ["cost"] = "O(1)", ["alloc"] = "0", ["doc"] = "Note line in category script", ["aliasOf"] = "dbg.log" });
        var events = new JsonArray(Enum.GetNames<EventKind>().Select(n =>
        {
            var u = LuaConstants.Upper(n);
            var d = EventDocs.TryGetValue(u, out var dd) ? dd : (Arg1: "-", Arg2: "-", Doc: "");
            return (JsonNode?)new JsonObject { ["name"] = u, ["arg1"] = d.Arg1, ["arg2"] = d.Arg2, ["doc"] = d.Doc };
        }).ToArray());
        var sensors = new JsonArray(ApiManifest.ZeroArgNumeric().Select(s => (JsonNode?)new JsonObject { ["name"] = s, ["returns"] = "number", ["doc"] = "" }).ToArray());
        var root = new JsonObject
        {
            ["apiVersion"] = ApiManifest.Version,
            ["functions"] = fns,
            ["constants"] = JsonSerializer.SerializeToNode(LuaConstants.All()),
            ["events"] = events,
            ["sensors"] = sensors,
            ["resources"] = new JsonArray("Movement", "Combat", "Cast", "Inventory", "Npc", "Chat"),
            ["procedures"] = new JsonArray(Enum.GetNames<ProcedureKind>().Select(n => (JsonNode?)LuaConstants.Upper(n)).ToArray()),
            ["jobs"] = new JsonArray(Enum.GetNames<JobKind>().Select(n => (JsonNode?)LuaConstants.Upper(n)).ToArray()),
            ["actionKinds"] = new JsonArray("MOVE", "MOVE_DIRECT", "STOP", "CAST", "BASH", "STOP_BASH", "TARGET", "USE", "SAY", "OPEN_SHOP", "TALK", "GATE", "REVIVE", "BUY", "SELL", "LOOT", "RAW"),
            ["actionArgs"] = JsonNode.Parse("""{"MOVE":["x","y","within"],"MOVE_DIRECT":["x","y"],"CAST":["skill","target"],"BASH":["target"],"TARGET":["target"],"USE":["slot"],"SAY":["text"],"OPEN_SHOP":["npc","reply"],"TALK":["npc","quest","reward"],"GATE":["gate","map"],"BUY":["item","lot"],"SELL":["slot","lot"],"LOOT":["target"],"RAW":["op","hex"]}"""),
        };
        return _json = root.ToJsonString();
    }
}

/// <summary>Observation endpoints (API_SPEC 8.3, 12.3, UI 13.7).</summary>
public static class ObservationEndpoints
{
    private static IResult Need(BotSupervisor s, string id, out BotRuntime b)
    {
        b = s.Get(id)!;
        if (b is null) return Http.Problem(404, $"no bot {id}");
        if (b.Agent is null) return Http.Problem(409, $"bot {id} is offline");
        return null!;
    }

    /// <summary>Map the routes.</summary>
    public static RouteGroupBuilder MapObservationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/bots/{id}/debug", (string id, BotSupervisor s) => Need(s, id, out var b) ?? (b.Frame is { } f ? Results.Json(f, Http.Json) : Http.Problem(409, "no debug frame yet (no brain, or not in the world)")));
        api.MapGet("/bots/{id}/transitions", (string id, long? since, string? layer, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var t = b.Brain?.Debug.Transitions() ?? [];
            return Results.Json(t.Where(x => x.Tick > (since ?? 0) && (layer is null || x.Layer == layer)).Reverse().Take(200), Http.Json);
        });
        api.MapGet("/bots/{id}/tick", (string id, int? last, BotSupervisor s) =>
        {
            if (Need(s, id, out var b) is { } err) return err;
            var buf = new TickSample[Math.Clamp(last ?? 1000, 1, 1200)];
            var n = b.Agent!.Stats.Last(buf);
            return Results.Json(buf.Take(n).Select(x => new
            {
                tick = x.Tick, totalUs = x.TotalUs, controlUs = x.ControlUs, applyUs = x.ApplyUs, actionsUs = x.ActionsUs, moveUs = x.MoveUs, combatUs = x.CombatUs,
                brainUs = x.BrainUs, publishUs = x.PublishUs, allocBytes = x.AllocBytes, publishAllocBytes = x.PublishAllocBytes, events = x.Events, luaInstructions = x.LuaInstructions,
            }), Http.Json);
        });
        api.MapGet("/bots/{id}/sensors", async (string id, BotSupervisor s) =>
        {
            if (Need(s, id, out var b) is { } err) return err;
            var a = b.Agent!;
            return Results.Json(await b.QueryAsync(_ => SensorFrames.Build(a)), Http.Json);   // built on the tick thread
        });
        api.MapGet("/bots/{id}/log", (string id, string? level, string? category, DateTime? from, DateTime? to, int? max, string? text, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var lv = Http.ParseLevel(level) ?? LogLevel.Trace;
            LogCategory? cat = Enum.TryParse<LogCategory>(category, true, out var c) ? c : null;
            var buf = new LogEntry[Math.Clamp(max ?? 500, 1, 16384)];
            var n = b.Log.Ring.Read(lv, cat, 0, long.MaxValue, buf);
            IEnumerable<object> lines = buf.Take(n).Where(e => (from is null || SystemClock.ToUtc(e.At) >= from.Value.ToUniversalTime()) && (to is null || SystemClock.ToUtc(e.At) <= to.Value.ToUniversalTime())
                                                              && (text is null || LogRenderer.Body(e).Contains(text, StringComparison.OrdinalIgnoreCase))).Select(e => Http.Line(e));
            return Results.Json(lines, Http.Json);
        });
        api.MapGet("/bots/{id}/log-levels", (string id, BotSupervisor s) => s.Get(id) is { } b ? Results.Json(Http.LogLevels(b), Http.Json) : Http.Problem(404, $"no bot {id}"));
        api.MapPut("/bots/{id}/log-levels", (string id, LogLevelRequest q, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            if (!Enum.TryParse<LogCategory>(q.Category, true, out var cat)) return Http.Problem(400, $"unknown category {q.Category}");
            if (Http.ParseLevel(q.Level) is not { } lv) return Http.Problem(400, "level: crit, note, info, debug, trace");
            b.Log.SetLevel(cat, lv, q.ExpiryMinutes is { } m ? TimeSpan.FromMinutes(m) : null);
            var levels = new Dictionary<string, string>(b.Record.LogLevels ?? new()) { [cat.ToString().ToLowerInvariant()] = lv.ToString().ToLowerInvariant() };
            if (q.ExpiryMinutes is null) s.Update(b, b.Record with { LogLevels = levels });
            return Results.Json(Http.LogLevels(b), Http.Json);
        });
        api.MapGet("/bots/{id}/packet-capture", (string id, BotSupervisor s) => s.Get(id) is { } b ? Results.Json(Http.Capture(b), Http.Json) : Http.Problem(404, $"no bot {id}"));
        api.MapPost("/bots/{id}/packet-capture", (string id, PacketCaptureRequest q, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            b.Capture.Configure(new PacketCaptureSettings(q.Enabled, q.Opcodes?.Select(x => (ushort)x).ToList(), q.Depts?.Select(x => (byte)x).ToList(),
                TimeSpan.FromMinutes(q.Minutes ?? 15), (long)((q.MaxMb ?? 64) * 1024 * 1024), q.Format == "binary"));
            return Results.Json(Http.Capture(b), Http.Json);
        });
        api.MapGet("/log/stats", (BotSupervisor s) => Results.Json(s.Bots.Select(b =>
        {
            var c = b.Log.Counters;
            return new { bot = b.Record.Id, lines = c.Lines, bytes = c.Bytes, drops = c.Dropped, collapsed = c.Collapsed };
        }), Http.Json));
        api.MapGet("/bots/{id}/recordings", (string id, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var list = new List<object>();
            var h = b.History();
            if (h.Count > 0) list.Add(new { id = "recent", kind = "ring", startedAt = h[0].T, endedAt = h[^1].T, frames = h.Count, ticks = h[^1].Tick - h[0].Tick, sizeBytes = 0 });
            if (Directory.Exists(b.RecordingsDir))
                foreach (var f in new DirectoryInfo(b.RecordingsDir).GetFiles("*.botrec"))
                    list.Add(new { id = Path.GetFileNameWithoutExtension(f.Name), kind = "botrec", startedAt = f.CreationTimeUtc, endedAt = f.LastWriteTimeUtc, frames = 0, ticks = 0, sizeBytes = f.Length });
            return Results.Json(list, Http.Json);
        });
        api.MapPost("/bots/{id}/recordings", async (string id, HttpRequest req, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            var on = true;
            if (req.ContentLength > 0) { var doc = await JsonDocument.ParseAsync(req.Body); if (doc.RootElement.TryGetProperty("enabled", out var e)) on = e.GetBoolean(); }
            if (on) return Results.Json(new { recording = true, file = b.StartRecording() }, Http.Json);
            b.StopRecording();
            return Results.Json(new { recording = false }, Http.Json);
        });
        api.MapGet("/bots/{id}/recordings/{rid}/frames", (string id, string rid, DateTime? from, DateTime? to, int? every, BotSupervisor s) =>
        {
            if (s.Get(id) is not { } b) return Http.Problem(404, $"no bot {id}");
            if (rid != "recent") return Http.Problem(501, "replaying a .botrec into frames needs its brain version on this host: not implemented yet; use the 'recent' ring");
            var h = b.History(); var sh = b.SensorHistory();
            var e = Math.Max(1, every ?? 1);
            var idx = Enumerable.Range(0, h.Count).Where(i => (from is null || h[i].T >= from.Value.ToUniversalTime()) && (to is null || h[i].T <= to.Value.ToUniversalTime())).Where((_, k) => k % e == 0).ToList();
            return Results.Json(new { debug = idx.Select(i => h[i]), sensors = idx.Where(i => i < sh.Count).Select(i => sh[i]) }, Http.Json);
        });
        api.MapGet("/bots/{id}/inventory", async (string id, BotSupervisor s) =>
        {
            if (Need(s, id, out var b) is { } err) return err;
            var r = await b.QueryAsync(c =>
            {
                var w = c.World; var slots = new List<object>(); var eq = new List<object>();
                var items = (ItemsModule?)null;
                for (var i = 0; i < w.Bag.Size; i++)
                {
                    var (it, lot) = w.Bag.At(i);
                    if (it < 0) continue;
                    var info = c.Data.Item(it);
                    slots.Add(new { slot = i, itemId = it, name = info?.Name ?? $"item {it}", lot, category = info is null ? "" : info.Equip > 0 ? "EQUIP" : info.ItemUseSkill is not ("-" or "") ? "CONSUMABLE" : info.SellPrice == 0 ? "QUEST" : "MATERIAL" });
                }
                for (var e = 0; e < 64; e++) { var it = w.Bag.Equipped(e); if (it >= 0) eq.Add(new { equipSlot = e, itemId = it, name = c.Data.Item(it)?.Name ?? $"item {it}" }); }
                _ = items;
                var shop = w.Npc.ShopAt < 0 ? null : new
                {
                    npc = w.Npc.ShopNpc, kind = $"0x{w.Npc.ShopKind:X4}", openedAt = SystemClock.ToUtc(w.Npc.ShopAt),
                    items = w.Npc.ShopItems.Select(x => new { slot = x.Slot, itemId = x.ItemId, name = c.Data.Item(x.ItemId)?.Name ?? $"item {x.ItemId}", price = c.Data.Item(x.ItemId)?.BuyPrice ?? -1 }).ToList(),
                };
                return new { bagSize = w.Bag.Size, money = w.Self.Money, hpStones = w.Self.HpStones, spStones = w.Self.SpStones, slots, equipped = eq, shop };
            });
            return Results.Json(r, Http.Json);
        });
        api.MapGet("/bots/{id}/quests", async (string id, BotSupervisor s) =>
        {
            if (Need(s, id, out var b) is { } err) return err;
            var r = await b.QueryAsync(c =>
            {
                var w = c.World; var o = new List<object>();
                foreach (var q in w.Quests.Active)
                {
                    var def = c.Data.Quest(q);
                    var objs = (def?.Objectives ?? []).Select((x, i) => new { kind = x.Kind.ToString().ToUpperInvariant(), mobId = x.MobId, itemId = x.ItemId, have = x.Kind == QuestObjectiveKind.Collect ? w.Bag.Count(x.ItemId) : w.Quests.Have(q, i + 1), need = x.Need }).ToList();
                    var ready = objs.Count > 0 && objs.All(x => x.kind == "TALK" || x.have >= x.need);
                    o.Add(new { id = q, name = def?.Name ?? $"quest {q}", state = ready ? "ready" : "active", status = w.Quests.Status(q), objectives = objs });
                }
                return o;
            });
            return Results.Json(r, Http.Json);
        });
        api.MapGet("/bots/{id}/skills", async (string id, BotSupervisor s) =>
        {
            if (Need(s, id, out var b) is { } err) return err;
            var r = await b.QueryAsync(c =>
            {
                var w = c.World;
                return w.Skills.Learned.Select(sid =>
                {
                    var sk = c.Data.Skill(sid);
                    var tags = SkillTags.Of(c.Data, sid);
                    var why = GameRules.CanUse(c, sid, w.Self.Handle);
                    return new
                    {
                        id = sid, name = sk?.Name ?? $"skill {sid}", rank = sk?.Step ?? 0,
                        tags = string.Join(",", Enum.GetValues<SkillTag>().Where(t => t != SkillTag.None && (tags & (int)t) != 0).Select(t => LuaConstants.Upper(t.ToString()))),
                        readyInMs = w.Skills.ReadyInMs(sid, sk?.DelayGroup ?? 0, c.Now), spCost = sk?.Sp ?? 0, canUse = why == CanUseReason.Ok,
                        whyNot = why == CanUseReason.Ok ? null : "R_" + LuaConstants.Upper(why.ToString()),
                    };
                }).ToList();
            });
            return Results.Json(r, Http.Json);
        });
        api.MapGet("/knowledge/mobs/{mobId:int}", (int mobId, HostServices h) =>
        {
            Estimate E(MobFact f) => h.Knowledge.Mob(mobId, f);
            double[] A(Estimate e) => [e.Value, e.Low, e.High, e.Samples];
            return Results.Json(new
            {
                mobId, name = h.Data.Mob(mobId)?.Name, detect = A(E(MobFact.DetectRadius)), leash = A(E(MobFact.ChaseDistance)), reach = A(E(MobFact.Reach)),
                interval = A(E(MobFact.AttackInterval)), hit = A(E(MobFact.HitAvg)),
            }, Http.Json);
        });
        api.MapGet("/metrics", (BotSupervisor s, HostServices h) =>
        {
            var sb = new StringBuilder();
            sb.Append("# HELP fiestabot_tick_us tick time per bot\n# TYPE fiestabot_tick_us gauge\n");
            foreach (var b in s.Bots)
            {
                if (b.Agent is not { } a) continue;
                var id = b.Record.Id;
                foreach (var q in new[] { 0.5, 0.99 })
                {
                    sb.Append(CultureInfo.InvariantCulture, $"fiestabot_tick_us{{bot=\"{id}\",step=\"total\",quantile=\"{q}\"}} {a.Stats.RecentQuantile(x => x.TotalUs, q):0.#}\n");
                    sb.Append(CultureInfo.InvariantCulture, $"fiestabot_tick_us{{bot=\"{id}\",step=\"brain\",quantile=\"{q}\"}} {a.Stats.RecentQuantile(x => x.BrainUs, q):0.#}\n");
                }
                sb.Append(CultureInfo.InvariantCulture, $"fiestabot_tick_alloc_bytes{{bot=\"{id}\",quantile=\"0.95\"}} {a.Stats.RecentQuantile(x => x.AllocBytes, 0.95):0}\n");
                var c = b.Log.Counters;
                sb.Append(CultureInfo.InvariantCulture, $"fiestabot_log_lines_total{{bot=\"{id}\"}} {c.Lines}\nfiestabot_log_dropped_total{{bot=\"{id}\"}} {c.Dropped}\nfiestabot_log_collapsed_total{{bot=\"{id}\"}} {c.Collapsed}\n");
                sb.Append(CultureInfo.InvariantCulture, $"fiestabot_transitions_total{{bot=\"{id}\"}} {b.Brain?.Debug.TransitionCount ?? 0}\n");
                sb.Append(CultureInfo.InvariantCulture, $"fiestabot_inbound_dropped_total{{bot=\"{id}\"}} {a.Inbound.Dropped}\n");
            }
            for (var g = 0; g <= 2; g++) sb.Append(CultureInfo.InvariantCulture, $"fiestabot_gc_collections_total{{gen=\"{g}\"}} {GC.CollectionCount(g)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"fiestabot_gc_pause_ms_total {GC.GetTotalPauseDuration().TotalMilliseconds:0}\n");
            sb.Append(CultureInfo.InvariantCulture, $"fiestabot_jobs_completed_total {h.Jobs.Completed}\nfiestabot_paths_total {h.Nav.Paths}\nfiestabot_path_us_total {h.Nav.PathMicros}\n");
            return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
        });
        return api;
    }
}

/// <summary>Line-based unified diff (LCS), for version diffs.</summary>
public static class TextDiff
{
    /// <summary>Unified diff of two texts.</summary>
    public static string Unified(string a, string b, string nameA, string nameB)
    {
        if (a == b) return "";
        var x = a.Replace("\r\n", "\n").Split('\n'); var y = b.Replace("\r\n", "\n").Split('\n');
        int n = x.Length, m = y.Length;
        if ((long)n * m > 4_000_000) return $"--- {nameA}\n+++ {nameB}\n@@ (files too large to diff) @@\n";
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--) for (var j = m - 1; j >= 0; j--) lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(char Op, string Line)>();
        int p = 0, q = 0;
        while (p < n && q < m)
        {
            if (x[p] == y[q]) { ops.Add((' ', x[p])); p++; q++; }
            else if (lcs[p + 1, q] >= lcs[p, q + 1]) ops.Add(('-', x[p++]));
            else ops.Add(('+', y[q++]));
        }
        while (p < n) ops.Add(('-', x[p++]));
        while (q < m) ops.Add(('+', y[q++]));
        var sb = new StringBuilder($"--- {nameA}\n+++ {nameB}\n");
        var ctx = 3;
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Op == ' ') continue;
            var start = Math.Max(0, i - ctx); var end = i;
            while (end < ops.Count && (ops[end].Op != ' ' || ops.Skip(end).Take(ctx * 2 + 1).Any(o => o.Op != ' '))) end++;
            end = Math.Min(ops.Count, end + ctx);
            sb.Append("@@\n");
            for (var k = start; k < end; k++) sb.Append(ops[k].Op).Append(ops[k].Line).Append('\n');
            i = end;
        }
        return sb.ToString();
    }
}
