using Fiesta.Bot.Agent;

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

/// <summary>Choose the initial state or machine by class family, level band or map.</summary>
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
    IReadOnlyList<ConditionDef>? Children = null);

/// <summary>Parses brain.yaml / JSON into the model and resolves `use` references.</summary>
public static class BrainLoader
{
    /// <summary>Parse YAML text (same model as JSON).</summary>
    public static BrainDefinition FromYaml(string yaml) => throw new NotImplementedException();
    /// <summary>Parse JSON text.</summary>
    public static BrainDefinition FromJson(string json) => throw new NotImplementedException();
    /// <summary>Serialise to canonical JSON (the hashed form).</summary>
    public static string ToCanonicalJson(BrainDefinition brain) => throw new NotImplementedException();
}

/// <summary>Lint severity.</summary>
public enum LintSeverity : byte { Info, Warning, Error }

/// <summary>One lint finding.</summary>
public sealed record LintResult(LintSeverity Severity, string Code, string Message, string? Module, int Line, string? Layer, string? State);

/// <summary>Validates a brain and its modules (API_SPEC 5.4, 6).</summary>
public static class BrainLinter
{
    /// <summary>Lint a definition and module sources against the API manifest.</summary>
    public static IReadOnlyList<LintResult> Lint(BrainDefinition brain, IReadOnlyDictionary<string, string> modules,
        Fiesta.Bot.Api.ApiManifest manifest) => throw new NotImplementedException();
}
