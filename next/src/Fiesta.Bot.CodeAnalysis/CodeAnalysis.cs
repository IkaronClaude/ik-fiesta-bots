using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fiesta.Bot.CodeAnalysis
{
    /// <summary>Emits a static [UnmanagedCallersOnly] lua_CFunction per [LuaFn] method (argument reads, result pushes,
    /// tuple -> multiple returns, LuaIter -> cached iterator closure, pending error instead of lua_error) plus the
    /// API manifest JSON.</summary>
    [Generator]
    public sealed class LuaBindingGenerator : IIncrementalGenerator
    {
        /// <summary>Register the syntax provider for [LuaModule] classes (skeleton: nothing emitted yet).</summary>
        public void Initialize(IncrementalGeneratorInitializationContext context) { }
    }

    /// <summary>FBOT001: blocking or time-reading calls in tick-path assemblies (Task.Wait, .Result,
    /// GetAwaiter().GetResult(), Thread.Sleep, await, DateTime.UtcNow) in Fiesta.Bot.Agent / Api / Brain.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class TickBlockingAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The rule.</summary>
        public static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "FBOT001", "Blocking call on the tick path", "{0} must not be used in tick-path code", "Performance",
            DiagnosticSeverity.Error, isEnabledByDefault: true);

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
        }
    }

    /// <summary>FBOT002: per-tick allocation patterns (LINQ, capturing lambdas, params arrays, boxing) in methods
    /// marked [HotPath]; a warning, per the allocation guideline.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HotPathAllocationAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The rule.</summary>
        public static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            "FBOT002", "Allocation on a hot path", "{0} allocates on a hot path", "Performance",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
        }
    }
}
