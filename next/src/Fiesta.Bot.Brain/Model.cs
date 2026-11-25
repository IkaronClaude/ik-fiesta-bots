using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fiesta.Bot.Agent;
using Fiesta.Bot.Api;

namespace Fiesta.Bot.Brain;

/// <summary>A brain definition (API_SPEC 5.2), parsed from brain.yaml / JSON.</summary>
public sealed record BrainDefinition(
    string Brain, int Api, string? Description, IReadOnlyList<string> Modules,
    IReadOnlyDictionary<string, object?> Blackboard, IReadOnlyDictionary<string, MachineDef> Machines,
    IReadOnlyList<LayerDef> Layers);

/// <summary>One layer: an independent hierarchical machine with a priority and rate.</summary>
public sealed record LayerDef(string Name, int Priority, LayerRate Rate, double BudgetMs, bool Enabled, MachineDef Machine);

/// <summary>Layer tick rate: every tick, or every N ms.</summary>
public readonly record struct LayerRate(bool EveryTick, int Ms)
{
    /// <summary>Every tick.</summary>
    public static readonly LayerRate Tick = new(true, 0);
}

/// <summary>A machine: initial state (or per-class/level selection) and states.</summary>
public sealed record MachineDef(string Initial, MachineSelect? Select, IReadOnlyDictionary<string, StateDef> States);

/// <summary>Choose the initial state by class family, level band or map.</summary>
public sealed record MachineSelect(SelectBy By, IReadOnlyDictionary<string, string> Cases);

/// <summary>Selection key.</summary>
public enum SelectBy : byte { ClassFamily, LevelBand, Map }

/// <summary>A state.</summary>
public sealed record StateDef(
    string? Enter, string? Tick, string? Exit, string? Use, MachineDef? Machine, Resource Claims,
    int BudgetMs, string? Progress, int MinDwellMs, int? TimeoutMs, bool Terminal, string? OnError,
    IReadOnlyList<TransitionDef> Transitions);

/// <summary>A transition with exactly one condition (possibly composite).</summary>
public sealed record TransitionDef(string To, int Priority, int HoldMs, int CooldownMs, string? Reason, ConditionDef Condition);

/// <summary>Condition kinds (API_SPEC 5.3).</summary>
public enum ConditionKind : byte { Lua, Event, ActionResult, Procedure, Job, After, Every, Value, Stuck, Always, All, Any, Not }

/// <summary>Comparison operators for value conditions.</summary>
public enum CompareOp : byte { Lt, Le, Gt, Ge, Eq, Ne }

/// <summary>A condition. Fields used depend on <see cref="Kind"/>.</summary>
public sealed record ConditionDef(
    ConditionKind Kind,
    string? Lua = null,
    EventKind? Event = null, string? Filter = null,
    string? ActionName = null, string? Result = null,
    ProcedureKind? Procedure = null, string? Job = null,
    int Ms = 0,
    string? Sensor = null, CompareOp Op = CompareOp.Lt, double Than = 0,
    IReadOnlyList<ConditionDef>? Children = null)
{
    /// <summary>Human-readable form (debugger, lint messages).</summary>
    public string Describe() => Kind switch
    {
        ConditionKind.Lua => Lua ?? "",
        ConditionKind.Event => $"on {Event}",
        ConditionKind.ActionResult => $"action {ActionName} {Result}",
        ConditionKind.Procedure => $"procedure {Procedure} {Result}",
        ConditionKind.Job => $"job {Job} {Result}",
        ConditionKind.After => $"after {Ms} ms",
        ConditionKind.Every => $"every {Ms} ms",
        ConditionKind.Value => $"{Sensor} {OpText(Op)} {Than.ToString(CultureInfo.InvariantCulture)}",
        ConditionKind.Stuck => "stuck",
        ConditionKind.Always => "always",
        ConditionKind.Not => "not (" + (Children is { Count: > 0 } c ? c[0].Describe() : "") + ")",
        _ => Kind.ToString().ToLowerInvariant() + " [" + string.Join(", ", (Children ?? []).Select(c => c.Describe())) + "]",
    };
    /// <summary>Operator text.</summary>
    public static string OpText(CompareOp op) => op switch { CompareOp.Lt => "<", CompareOp.Le => "<=", CompareOp.Gt => ">", CompareOp.Ge => ">=", CompareOp.Eq => "==", _ => "~=" };
}

/// <summary>A brain definition parse error.</summary>
public sealed class BrainFormatException(string message) : Exception(message);

/// <summary>Parses brain.yaml / JSON into the model.</summary>
public static class BrainLoader
{
    /// <summary>Event name aliases accepted in `on:` (beyond the ev.* names in camelCase / UPPER_CASE).</summary>
    public static readonly IReadOnlyDictionary<string, EventKind> EventAliases = new Dictionary<string, EventKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["aggressorAppeared"] = EventKind.AggroGained, ["aggro"] = EventKind.AggroGained, ["procedureDone"] = EventKind.ProcedureDone,
        ["died"] = EventKind.Died, ["dead"] = EventKind.Died,
    };

    /// <summary>Parse YAML text (JSON is valid YAML).</summary>
    public static BrainDefinition FromYaml(string yaml)
    {
        object? root;
        try { root = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(yaml); }
        catch (YamlDotNet.Core.YamlException e) { throw new BrainFormatException($"YAML line {e.Start.Line}: {e.Message}"); }
        return FromTree(ToJson(root) as JsonObject ?? throw new BrainFormatException("the brain definition must be a mapping"));
    }

    /// <summary>Parse JSON text.</summary>
    public static BrainDefinition FromJson(string json)
    {
        try { return FromTree(JsonNode.Parse(json) as JsonObject ?? throw new BrainFormatException("the brain definition must be an object")); }
        catch (JsonException e) { throw new BrainFormatException("JSON: " + e.Message); }
    }

    /// <summary>Parse either (JSON when it starts with '{').</summary>
    public static BrainDefinition Parse(string text) => text.TrimStart().StartsWith('{') ? FromJson(text) : FromYaml(text);

    private static JsonNode? ToJson(object? o) => o switch
    {
        null => null,
        IDictionary<object, object> d => new JsonObject(d.Select(kv => KeyValuePair.Create(kv.Key.ToString()!, ToJson(kv.Value)))),
        IList<object> l => new JsonArray(l.Select(ToJson).ToArray()),
        string s when s is "true" or "false" => JsonValue.Create(s == "true"),
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => JsonValue.Create(i),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) => JsonValue.Create(f),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(o.ToString()),
    };

    private static string? S(JsonObject o, string k) => o[k] is JsonValue v ? v.ToString() : null;
    private static int I(JsonObject o, string k, int d) => o[k] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? (int)x : d;
    private static double D(JsonObject o, string k, double d) => o[k] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : d;
    private static bool B(JsonObject o, string k, bool d) => o[k] is JsonValue v ? v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) : d;

    /// <summary>Build from a JSON tree.</summary>
    public static BrainDefinition FromTree(JsonObject o)
    {
        var name = S(o, "brain") ?? throw new BrainFormatException("missing `brain`");
        var api = I(o, "api", ApiManifest.Version);
        var modules = (o["modules"] as JsonArray)?.Select(x => x!.ToString()).ToList() ?? [];
        var bb = (o["blackboard"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.ToJsonString()) ?? new Dictionary<string, object?>();
        var machines = new Dictionary<string, MachineDef>();
        if (o["machines"] is JsonObject ms) foreach (var kv in ms) machines[kv.Key] = Machine(kv.Value as JsonObject ?? throw new BrainFormatException($"machine {kv.Key} must be a mapping"), kv.Key);
        var layers = new List<LayerDef>();
        if (o["layers"] is not JsonArray ls || ls.Count == 0) throw new BrainFormatException("missing `layers`");
        foreach (var l in ls)
        {
            if (l is not JsonObject lo) throw new BrainFormatException("a layer must be a mapping");
            var ln = S(lo, "name") ?? throw new BrainFormatException("a layer needs a `name`");
            var rate = lo["rate"] is JsonValue rv && rv.ToString() != "tick" ? new LayerRate(false, Math.Max(50, I(lo, "rate", 50))) : LayerRate.Tick;
            var m = lo["machine"] as JsonObject ?? throw new BrainFormatException($"layer {ln} needs a `machine`");
            layers.Add(new LayerDef(ln, I(lo, "priority", 50), rate, D(lo, "budgetMs", 2), B(lo, "enabled", true), Machine(m, ln)));
        }
        return new BrainDefinition(name, api, S(o, "description"), modules, bb, machines, layers);
    }

    private static MachineDef Machine(JsonObject m, string where)
    {
        var states = new Dictionary<string, StateDef>();
        if (m["states"] is not JsonObject ss || ss.Count == 0) throw new BrainFormatException($"machine {where} needs `states`");
        foreach (var kv in ss) states[kv.Key] = State(kv.Value as JsonObject ?? new JsonObject(), $"{where}.{kv.Key}");
        MachineSelect? sel = null;
        if (m["select"] is JsonObject so)
        {
            var by = (S(so, "by") ?? "classFamily").ToLowerInvariant() switch { "levelband" => SelectBy.LevelBand, "map" => SelectBy.Map, _ => SelectBy.ClassFamily };
            sel = new MachineSelect(by, (so["cases"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "") ?? new Dictionary<string, string>());
        }
        var initial = S(m, "initial") ?? states.Keys.First();
        return new MachineDef(initial, sel, states);
    }

    private static StateDef State(JsonObject s, string where)
    {
        var claims = Resource.None;
        if (s["claims"] is JsonArray ca)
            foreach (var c in ca)
                claims |= Enum.TryParse<Resource>(c!.ToString(), true, out var r) ? r : throw new BrainFormatException($"{where}: unknown resource '{c}'");
        var trans = new List<TransitionDef>();
        if (s["transitions"] is JsonArray ta)
            foreach (var t in ta) trans.Add(Transition(t as JsonObject ?? throw new BrainFormatException($"{where}: a transition must be a mapping"), where));
        return new StateDef(S(s, "enter"), S(s, "tick"), S(s, "exit"), S(s, "use"),
            s["machine"] is JsonObject sub ? Machine(sub, where) : null, claims,
            I(s, "budget", 60000), S(s, "progress"), I(s, "minDwell", 0), s["timeout"] is null ? null : I(s, "timeout", 0),
            B(s, "terminal", false), S(s, "onError"), trans);
    }

    private static TransitionDef Transition(JsonObject t, string where)
        => new(S(t, "to") ?? throw new BrainFormatException($"{where}: a transition needs `to`"), I(t, "priority", 0), I(t, "hold", 0),
               I(t, "cooldown", 0), S(t, "reason"), Condition(t, where));

    /// <summary>The condition of a transition (or of a composite child).</summary>
    public static ConditionDef Condition(JsonObject t, string where)
    {
        if (t["all"] is JsonArray all) return new ConditionDef(ConditionKind.All, Children: all.Select(c => Condition((JsonObject)c!, where)).ToList());
        if (t["any"] is JsonArray any) return new ConditionDef(ConditionKind.Any, Children: any.Select(c => Condition((JsonObject)c!, where)).ToList());
        if (t["not"] is JsonObject not) return new ConditionDef(ConditionKind.Not, Children: [Condition(not, where)]);
        if (S(t, "when") is { } when) return new ConditionDef(ConditionKind.Lua, Lua: when);
        if (S(t, "on") is { } on)
        {
            if (on.Equals("action", StringComparison.OrdinalIgnoreCase)) return new ConditionDef(ConditionKind.ActionResult, ActionName: S(t, "action"), Result: S(t, "result") ?? "acked");
            if (on.Equals("noAggro", StringComparison.OrdinalIgnoreCase)) return new ConditionDef(ConditionKind.Value, Sensor: "threat.onUs", Op: CompareOp.Eq, Than: 0);
            if (on.Equals("procedureDone", StringComparison.OrdinalIgnoreCase) && S(t, "procedure") is { } pn)
                return new ConditionDef(ConditionKind.Procedure, Procedure: ParseProcedure(pn, where), Result: S(t, "result") ?? "ok");
            return new ConditionDef(ConditionKind.Event, Event: ParseEvent(on, where), Filter: S(t, "filter"));
        }
        if (S(t, "procedure") is { } proc) return new ConditionDef(ConditionKind.Procedure, Procedure: ParseProcedure(proc, where), Result: S(t, "result") ?? "ok");
        if (S(t, "job") is { } job) return new ConditionDef(ConditionKind.Job, Job: job, Result: S(t, "result"));
        if (t["after"] is not null) return new ConditionDef(ConditionKind.After, Ms: I(t, "after", 0));
        if (t["every"] is not null) return new ConditionDef(ConditionKind.Every, Ms: I(t, "every", 1000));
        if (S(t, "value") is { } sensor)
        {
            var op = (S(t, "op") ?? "<") switch { "<" => CompareOp.Lt, "<=" => CompareOp.Le, ">" => CompareOp.Gt, ">=" => CompareOp.Ge, "==" => CompareOp.Eq, "~=" or "!=" => CompareOp.Ne, var x => throw new BrainFormatException($"{where}: unknown op '{x}'") };
            return new ConditionDef(ConditionKind.Value, Sensor: sensor, Op: op, Than: D(t, "than", 0));
        }
        if (B(t, "stuck", false)) return new ConditionDef(ConditionKind.Stuck);
        if (B(t, "always", false)) return new ConditionDef(ConditionKind.Always);
        throw new BrainFormatException($"{where}: transition to '{S(t, "to")}' has no condition");
    }

    /// <summary>Event by name (camelCase, UPPER_CASE or an alias).</summary>
    public static EventKind ParseEvent(string s, string where)
    {
        if (EventAliases.TryGetValue(s, out var a)) return a;
        var norm = s.Replace("_", "");
        foreach (var k in Enum.GetValues<EventKind>()) if (k.ToString().Equals(norm, StringComparison.OrdinalIgnoreCase)) return k;
        throw new BrainFormatException($"{where}: unknown event '{s}'");
    }

    private static ProcedureKind ParseProcedure(string s, string where)
    {
        var norm = s.Replace("_", "");
        foreach (var k in Enum.GetValues<ProcedureKind>()) if (k.ToString().Equals(norm, StringComparison.OrdinalIgnoreCase)) return k;
        throw new BrainFormatException($"{where}: unknown procedure '{s}'");
    }

    /// <summary>Serialise to canonical JSON (the hashed form; deterministic order).</summary>
    public static string ToCanonicalJson(BrainDefinition brain) => JsonSerializer.Serialize(brain, Canon);
    private static readonly JsonSerializerOptions Canon = new() { WriteIndented = false, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}

/// <summary>Lint severity.</summary>
public enum LintSeverity : byte { Info, Warning, Error }

/// <summary>One lint finding.</summary>
public sealed record LintResult(LintSeverity Severity, string Code, string Message, string? Module, int Line, string? Layer, string? State);

/// <summary>Validates a brain and its modules (API_SPEC 5.4, 6).</summary>
public static class BrainLinter
{
    /// <summary>Lint a definition and module sources against the API manifest. Lua syntax is checked by compiling each
    /// module in a scratch VM.</summary>
    public static IReadOnlyList<LintResult> Lint(BrainDefinition brain, IReadOnlyDictionary<string, string> modules, IReadOnlySet<string>? sensors = null)
    {
        var o = new List<LintResult>();
        if (brain.Api != ApiManifest.Version) o.Add(new(LintSeverity.Error, "API", $"brain targets api {brain.Api}, this host serves {ApiManifest.Version}", null, 0, null, null));
        foreach (var m in brain.Modules)
            if (!modules.ContainsKey(StripPin(m)) && !BuiltinLibraries.Sources.ContainsKey(StripPin(m)))
                o.Add(new(LintSeverity.Error, "MODULE", $"module '{m}' is not in the version's module set", m, 0, null, null));
        var api = ApiManifest.Names();
        var moduleNames = new HashSet<string>(modules.Keys.Concat(BuiltinLibraries.Sources.Keys).Select(ShortName));
        foreach (var (path, src) in modules)
        {
            var err = LuaSyntax.Check(path, src);
            if (err is not null) o.Add(new(LintSeverity.Error, "SYNTAX", err.Value.Message, path, err.Value.Line, null, null));
            // unknown api names: module.fn references to API modules that the manifest does not have
            foreach (var (line, reference) in ApiRefs(src))
                if (!api.Contains(reference)) o.Add(new(LintSeverity.Error, "API_NAME", $"unknown API function '{reference}'", path, line, null, null));
            foreach (var (line, g) in GlobalWrites(src))
                o.Add(new(LintSeverity.Warning, "GLOBAL", $"writes global '{g}' (use bb.* for state)", path, line, null, null));
        }
        foreach (var layer in brain.Layers)
        {
            LintMachine(layer.Machine, layer.Name, brain, o, moduleNames, sensors);
        }
        var names = brain.Layers.GroupBy(l => l.Name).Where(g => g.Count() > 1);
        foreach (var g in names) o.Add(new(LintSeverity.Error, "LAYER", $"duplicate layer '{g.Key}'", null, 0, g.Key, null));
        return o;
    }

    private static void LintMachine(MachineDef m, string layer, BrainDefinition brain, List<LintResult> o, HashSet<string> moduleNames, IReadOnlySet<string>? sensors)
    {
        if (!m.States.ContainsKey(m.Initial)) o.Add(new(LintSeverity.Error, "INITIAL", $"initial state '{m.Initial}' does not exist", null, 0, layer, null));
        var reached = new HashSet<string> { m.Initial };
        if (m.Select is { } sel) foreach (var c in sel.Cases.Values) reached.Add(c);
        foreach (var (name, s) in m.States)
        {
            foreach (var t in s.Transitions)
            {
                var target = t.To.Contains(':') ? t.To[(t.To.IndexOf(':') + 1)..] : t.To;
                if (t.To == "^") continue;
                if (t.To.Contains(':'))
                {
                    var mn = t.To[..t.To.IndexOf(':')];
                    if (!brain.Machines.TryGetValue(mn, out var other) || !other.States.ContainsKey(target))
                        o.Add(new(LintSeverity.Error, "TARGET", $"transition to unknown '{t.To}'", null, 0, layer, name));
                }
                else if (!m.States.ContainsKey(target)) o.Add(new(LintSeverity.Error, "TARGET", $"transition to unknown state '{t.To}'", null, 0, layer, name));
                else reached.Add(target);
                LintCondition(t.Condition, layer, name, o, sensors);
            }
            var exits = s.Transitions.Any(t => t.To != name);
            if (!exits && !s.Terminal) o.Add(new(LintSeverity.Error, "NO_EXIT", "state has no transition to another state and is not terminal", null, 0, layer, name));
            var timed = s.Transitions.Any(t => HasKind(t.Condition, ConditionKind.Stuck) || HasKind(t.Condition, ConditionKind.After)) || s.TimeoutMs is not null;
            if (!s.Terminal && !timed)
                o.Add(new(s.BudgetMs == 0 ? LintSeverity.Error : LintSeverity.Warning, "NO_TIMEOUT", "no stuck / after / timeout exit: the state can stall silently", null, 0, layer, name));
            foreach (var fn in new[] { s.Enter, s.Tick, s.Exit })
                if (fn is not null && IsRef(fn) && fn.Contains('.') && !moduleNames.Contains(fn[..fn.LastIndexOf('.')]) && !ApiManifest.Names().Contains(fn))
                    o.Add(new(LintSeverity.Error, "FN_REF", $"'{fn}' refers to module '{fn[..fn.LastIndexOf('.')]}' which the brain does not load", null, 0, layer, name));
            if (s.Machine is { } sub) LintMachine(sub, layer, brain, o, moduleNames, sensors);
            if (s.Use is { } use && !use.Contains("${") && !brain.Machines.ContainsKey(use))
                o.Add(new(LintSeverity.Error, "USE", $"`use: {use}` names no machine in `machines`", null, 0, layer, name));
        }
        foreach (var s in m.States.Keys)
            if (!reached.Contains(s)) o.Add(new(LintSeverity.Warning, "UNREACHABLE", "no transition leads here", null, 0, layer, s));
    }

    private static void LintCondition(ConditionDef c, string layer, string state, List<LintResult> o, IReadOnlySet<string>? sensors)
    {
        if (c.Kind == ConditionKind.Value && sensors is not null && c.Sensor is { } sn && !sensors.Contains(sn))
            o.Add(new(LintSeverity.Error, "SENSOR", $"unknown sensor '{sn}'", null, 0, layer, state));
        if (c.Kind == ConditionKind.Lua && c.Lua is { } code && LuaSyntax.Check("condition", IsRef(code) ? "return " + code : "return (" + code + ")") is { } err)
            o.Add(new(LintSeverity.Error, "SYNTAX", $"condition: {err.Message}", null, 0, layer, state));
        foreach (var ch in c.Children ?? []) LintCondition(ch, layer, state, o, sensors);
    }

    private static bool HasKind(ConditionDef c, ConditionKind k) => c.Kind == k || (c.Kind is ConditionKind.Any && (c.Children ?? []).Any(x => HasKind(x, k)));

    /// <summary>Is a function reference a dotted identifier path (module.fn) rather than inline code?</summary>
    public static bool IsRef(string s) => s.Length > 0 && s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.') && !char.IsDigit(s[0]);

    /// <summary>Module path without a version pin.</summary>
    public static string StripPin(string m) => m.Contains('@') ? m[..m.IndexOf('@')] : m;
    /// <summary>The name a module is reachable by in conditions and state functions: its last path segment.</summary>
    public static string ShortName(string path) => StripPin(path)[(StripPin(path).LastIndexOf('/') + 1)..];

    private static readonly HashSet<string> ApiModules = ["self", "ents", "threat", "combat", "skills", "act", "items", "quests", "nav", "npc", "social", "know", "time", "ev", "world", "dbg"];

    private static IEnumerable<(int Line, string Ref)> ApiRefs(string src)
    {
        var lines = src.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            var c = l.IndexOf("--", StringComparison.Ordinal);
            if (c >= 0) l = l[..c];
            for (var j = 0; j < l.Length; j++)
            {
                if (!char.IsLetter(l[j]) || (j > 0 && (char.IsLetterOrDigit(l[j - 1]) || l[j - 1] is '_' or '.' or ':'))) continue;
                var k = j; while (k < l.Length && (char.IsLetterOrDigit(l[k]) || l[k] == '_')) k++;
                var mod = l[j..k];
                if (k < l.Length && l[k] == '.' && ApiModules.Contains(mod))
                {
                    var e = k + 1; while (e < l.Length && (char.IsLetterOrDigit(l[e]) || l[e] == '_')) e++;
                    var fn = l[(k + 1)..e];
                    if (fn.Length > 0 && !fn.All(ch => char.IsUpper(ch) || ch == '_' || char.IsDigit(ch)) && e < l.Length && l[e] == '(')
                        yield return (i + 1, mod + "." + fn);
                }
                j = k;
            }
        }
    }

    private static IEnumerable<(int Line, string Name)> GlobalWrites(string src)
    {
        var lines = src.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("--") || l.StartsWith("local ") || !l.Contains('=') || l.Contains("==")) continue;
            var eq = l.IndexOf('=');
            var lhs = l[..eq].Trim();
            if (lhs.Length > 0 && lhs.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !char.IsDigit(lhs[0]) && !l.StartsWith("for ") && lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]))
                yield return (i + 1, lhs);
        }
    }
}

/// <summary>Lua syntax check in a scratch VM (no API registered; compile only).</summary>
public static unsafe class LuaSyntax
{
    /// <summary>Null when the chunk compiles, else the message and line.</summary>
    public static (string Message, int Line)? Check(string chunkName, string source)
    {
        var L = Lua54.luaL_newstate();
        try
        {
            var b = Encoding.UTF8.GetBytes(source);
            var n = LuaRegistrar.Z("@" + chunkName);
            int r;
            fixed (byte* p = b) fixed (byte* np = n) r = Lua54.luaL_loadbufferx(L, p, (nuint)b.Length, np, null);
            if (r == 0) return null;
            var msg = LuaPush.ToStr(L, -1) ?? "syntax error";
            var line = 0;
            var c1 = msg.IndexOf(':');
            var c2 = c1 >= 0 ? msg.IndexOf(':', c1 + 1) : -1;
            if (c1 >= 0 && c2 > c1) int.TryParse(msg[(c1 + 1)..c2], out line);
            return (msg, line);
        }
        finally { Lua54.lua_close(L); }
    }
}

/// <summary>Host-provided libraries (`lib/*`, read-only, versioned with the API).</summary>
public static class BuiltinLibraries
{
    /// <summary>Sources by path.</summary>
    public static IReadOnlyDictionary<string, string> Sources { get; } = Load();

    private static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        var asm = typeof(BuiltinLibraries).Assembly;
        foreach (var r in asm.GetManifestResourceNames())
        {
            if (!r.EndsWith(".lua")) continue;
            var key = r[(r.IndexOf("lib.", StringComparison.Ordinal))..];   // Fiesta.Bot.Brain.lib.core.lua -> lib.core.lua
            key = "lib/" + key[4..^4].Replace('.', '/');
            using var s = asm.GetManifestResourceStream(r)!;
            using var rd = new StreamReader(s);
            d[key] = rd.ReadToEnd();
        }
        return d;
    }
}

/// <summary>Compiles a brain version: parse, resolve modules, lint, hash.</summary>
public static class BrainCompiler
{
    /// <summary>Compile; errors are in the lint results (Severity Error = not applicable).</summary>
    public static CompiledBrain Compile(BrainDefinition definition, IReadOnlyDictionary<string, string> modules, IReadOnlySet<string>? sensors = null)
    {
        var lint = BrainLinter.Lint(definition, modules, sensors);
        return new CompiledBrain(Hash(definition, modules, ApiManifest.Version), definition, modules, lint);
    }

    /// <summary>Content hash of definition + modules + API version (12 hex).</summary>
    public static string Hash(BrainDefinition definition, IReadOnlyDictionary<string, string> modules, int apiVersion)
    {
        var sb = new StringBuilder();
        sb.Append(apiVersion).Append('\n').Append(BrainLoader.ToCanonicalJson(definition)).Append('\n');
        foreach (var kv in modules.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('\0').Append(kv.Value).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..12];
    }

    /// <summary>Content hash of one module source (12 hex).</summary>
    public static string ModuleHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..12];
}

/// <summary>A brain version compiled and validated (definition + module sources + lint).</summary>
public sealed record CompiledBrain(string Hash, BrainDefinition Definition, IReadOnlyDictionary<string, string> Modules,
    IReadOnlyList<LintResult> Lint)
{
    /// <summary>Applicable (no lint errors).</summary>
    public bool Ok => Lint.All(l => l.Severity != LintSeverity.Error);
}

/// <summary>Saved brain state for resume after relog/redeploy.</summary>
public sealed record BrainCheckpoint(string BotId, DateTime At, string VersionHash,
    IReadOnlyDictionary<string, IReadOnlyList<string>> LayerPaths, string BotBlackboardJson);
