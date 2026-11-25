using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fiesta.Bot.CodeAnalysis
{
    /// <summary>Emits a static [UnmanagedCallersOnly] lua_CFunction per [LuaFn] method (argument reads, result pushes,
    /// tuple -> multiple returns, LuaIter -> cached iterator function, pending error instead of lua_error) plus the
    /// API manifest JSON and the registration of every module table.</summary>
    [Generator]
    public sealed class LuaBindingGenerator : IIncrementalGenerator
    {
        private sealed class Fn
        {
            public string Method = "", LuaName = "", Doc = "", Returns = "", Cost = "O1", Alloc = "None", Alias = "";
            public List<(string Name, string Type, string? Default)> Params = new List<(string, string, string?)>();
            public string RetType = "";
            public List<string> TupleTypes = new List<string>();
        }

        private sealed class Module
        {
            public string Ns = "", Class = "", LuaName = "", Doc = "";
            public List<Fn> Fns = new List<Fn>();
        }

        /// <summary>Register the syntax provider for [LuaModule] classes.</summary>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var mods = context.SyntaxProvider.ForAttributeWithMetadataName(
                "Fiesta.Bot.Api.LuaModuleAttribute",
                (n, _) => n is ClassDeclarationSyntax,
                (ctx, _) => Read((INamedTypeSymbol)ctx.TargetSymbol, ctx.Attributes[0]));
            context.RegisterSourceOutput(mods.Collect(), Emit);
        }

        private static string Short(ITypeSymbol t)
        {
            switch (t.SpecialType)
            {
                case SpecialType.System_Int32: return "int";
                case SpecialType.System_Int64: return "long";
                case SpecialType.System_Double: return "double";
                case SpecialType.System_Boolean: return "bool";
                case SpecialType.System_String: return "string";
                case SpecialType.System_Void: return "void";
            }
            if (t.Name == "LuaIter") return "iter";
            if (t.Name == "LuaTableArg") return "table";
            if (t.Name == "LuaAny") return "any";
            if (t is INamedTypeSymbol nt && nt.IsTupleType) return "tuple";
            if (t.NullableAnnotation == NullableAnnotation.Annotated && t is INamedTypeSymbol n2 && n2.IsValueType && n2.TypeArguments.Length == 1)
                return Short(n2.TypeArguments[0]) + "?";
            return t.ToDisplayString();
        }

        private static Module Read(INamedTypeSymbol cls, AttributeData attr)
        {
            var m = new Module
            {
                Ns = cls.ContainingNamespace.ToDisplayString(),
                Class = cls.Name,
                LuaName = (string)attr.ConstructorArguments[0].Value!,
                Doc = Summary(cls.GetDocumentationCommentXml()),
            };
            foreach (var meth in cls.GetMembers().OfType<IMethodSymbol>())
            {
                var a = meth.GetAttributes().FirstOrDefault(x => x.AttributeClass?.Name == "LuaFnAttribute");
                if (a == null) continue;
                var f = new Fn { Method = meth.Name, Doc = Summary(meth.GetDocumentationCommentXml()) };
                var name = a.NamedArguments.FirstOrDefault(x => x.Key == "Name").Value.Value as string;
                f.LuaName = name ?? char.ToLowerInvariant(meth.Name[0]) + meth.Name.Substring(1);
                f.Alias = a.NamedArguments.FirstOrDefault(x => x.Key == "GlobalAlias").Value.Value as string ?? "";
                var cost = a.NamedArguments.FirstOrDefault(x => x.Key == "Cost").Value;
                if (cost.Value is int ci) f.Cost = new[] { "O1", "Ok", "On", "Cached", "Job" }[ci];
                var alloc = a.NamedArguments.FirstOrDefault(x => x.Key == "Alloc").Value;
                if (alloc.Value is int ai) f.Alloc = new[] { "None", "LuaString", "Iterator", "Small" }[ai];
                foreach (var p in meth.Parameters)
                {
                    string? def = null;
                    if (p.HasExplicitDefaultValue)
                    {
                        var v = p.ExplicitDefaultValue;
                        def = v == null ? "null" : v is bool b ? (b ? "true" : "false") : v is string s ? "\"" + s + "\""
                            : v is double d ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d"
                            : v.ToString();
                    }
                    f.Params.Add((p.Name, Short(p.Type), def));
                }
                f.RetType = Short(meth.ReturnType);
                if (f.RetType == "tuple")
                    foreach (var e in ((INamedTypeSymbol)meth.ReturnType).TupleElements) f.TupleTypes.Add(Short(e.Type));
                f.Returns = f.RetType == "tuple" ? string.Join(", ", f.TupleTypes) : f.RetType;
                m.Fns.Add(f);
            }
            return m;
        }

        private static string Summary(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            var i = xml!.IndexOf("<summary>"); var j = xml.IndexOf("</summary>");
            if (i < 0 || j < 0) return "";
            var s = xml.Substring(i + 9, j - i - 9);
            var sb = new StringBuilder();
            var inTag = false;
            foreach (var ch in s) { if (ch == '<') inTag = true; else if (ch == '>') inTag = false; else if (!inTag) sb.Append(ch); }
            return string.Join(" ", sb.ToString().Split(new[] { ' ', '\n', '\r', '\t' }, System.StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Json(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static void Emit(SourceProductionContext spc, ImmutableArray<Module> mods)
        {
            var ordered = mods.OrderBy(m => m.LuaName).ToList();
            for (var k = 0; k < ordered.Count; k++)
            {
                var m = ordered[k];
                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("#nullable enable");
                sb.AppendLine("using System;");
                sb.AppendLine("using System.Runtime.CompilerServices;");
                sb.AppendLine("using System.Runtime.InteropServices;");
                sb.AppendLine("using Fiesta.Bot.Api;");
                sb.AppendLine($"namespace {m.Ns};");
                sb.AppendLine($"unsafe partial class {m.Class}");
                sb.AppendLine("{");
                sb.AppendLine($"    internal const int __ModuleIndex = {k};");
                foreach (var f in m.Fns)
                {
                    sb.AppendLine("    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]");
                    sb.AppendLine($"    private static int __lua_{f.Method}(IntPtr L)");
                    sb.AppendLine("    {");
                    sb.AppendLine($"        var __m = ({m.Class})ApiScope.Modules![{k}];");
                    sb.AppendLine("        int __top = Lua54.lua_gettop(L);");
                    var args = new List<string>();
                    var tableIdx = -1;
                    for (var i = 0; i < f.Params.Count; i++)
                    {
                        var (pn, pt, def) = f.Params[i];
                        var idx = i + 1;
                        var v = "__a" + i;
                        var missing = $"(__top < {idx} || Lua54.lua_type(L, {idx}) <= 0)";
                        switch (pt)
                        {
                            case "int":
                            case "long":
                            {
                                var cast = pt == "int" ? "(int)" : "";
                                var fallback = def ?? "0";
                                sb.AppendLine($"        {pt} {v};");
                                sb.AppendLine($"        if ({missing}) {{ {(def == null ? $"ApiScope.ArgError(\"{m.LuaName}.{f.LuaName}\", \"{pn} missing\"); Lua54.lua_pushnil(L); return 1;" : $"{v} = {fallback};")} }}");
                                sb.AppendLine($"        else {{ int __ok; var __x = Lua54.lua_tointegerx(L, {idx}, &__ok); if (__ok == 0) {{ var __d = Lua54.lua_tonumberx(L, {idx}, &__ok); if (__ok == 0) {{ ApiScope.ArgError(\"{m.LuaName}.{f.LuaName}\", \"{pn} must be a number\"); Lua54.lua_pushnil(L); return 1; }} __x = (long)__d; }} {v} = {cast}__x; }}");
                                break;
                            }
                            case "double":
                                sb.AppendLine($"        double {v};");
                                sb.AppendLine($"        if ({missing}) {{ {(def == null ? $"ApiScope.ArgError(\"{m.LuaName}.{f.LuaName}\", \"{pn} missing\"); Lua54.lua_pushnil(L); return 1;" : $"{v} = {def};")} }}");
                                sb.AppendLine($"        else {{ int __ok; {v} = Lua54.lua_tonumberx(L, {idx}, &__ok); if (__ok == 0) {{ ApiScope.ArgError(\"{m.LuaName}.{f.LuaName}\", \"{pn} must be a number\"); Lua54.lua_pushnil(L); return 1; }} }}");
                                break;
                            case "bool":
                                sb.AppendLine($"        bool {v} = {missing} ? {def ?? "false"} : Lua54.lua_toboolean(L, {idx}) != 0;");
                                break;
                            case "string":
                                sb.AppendLine($"        string? {v} = {missing} ? {def ?? "null"} : LuaPush.ToStr(L, {idx});");
                                break;
                            case "table":
                                tableIdx = idx;
                                sb.AppendLine($"        if (Lua54.lua_type(L, {idx}) != 5) {{ ApiScope.ArgError(\"{m.LuaName}.{f.LuaName}\", \"{pn} must be a table\"); Lua54.lua_pushnil(L); return 1; }}");
                                sb.AppendLine($"        var {v} = new LuaTableArg({idx});");
                                break;
                            case "any":
                                sb.AppendLine($"        var {v} = new LuaAny(L, {idx}, __top);");
                                break;
                            default:
                                sb.AppendLine($"        #error unsupported parameter type {pt} in {m.Class}.{f.Method}");
                                break;
                        }
                        args.Add(v);
                    }
                    var call = $"__m.{f.Method}({string.Join(", ", args)})";
                    switch (f.RetType)
                    {
                        case "void": sb.AppendLine($"        {call}; return 0;"); break;
                        case "int": case "long": sb.AppendLine($"        Lua54.lua_pushinteger(L, {call}); return 1;"); break;
                        case "double": sb.AppendLine($"        Lua54.lua_pushnumber(L, {call}); return 1;"); break;
                        case "bool": sb.AppendLine($"        Lua54.lua_pushboolean(L, {call} ? 1 : 0); return 1;"); break;
                        case "string": sb.AppendLine($"        LuaPush.Str(L, {call}); return 1;"); break;
                        case "int?": case "long?": sb.AppendLine($"        var __r = {call}; if (__r is null) Lua54.lua_pushnil(L); else Lua54.lua_pushinteger(L, __r.Value); return 1;"); break;
                        case "double?": sb.AppendLine($"        var __r = {call}; if (__r is null) Lua54.lua_pushnil(L); else Lua54.lua_pushnumber(L, __r.Value); return 1;"); break;
                        case "iter":
                            if (tableIdx > 0) sb.AppendLine($"        var __it = {call}; return LuaPush.Fill(L, {tableIdx}, __it);");
                            else sb.AppendLine($"        var __it = {call}; return LuaPush.Iter(L, __it);");
                            break;
                        case "tuple":
                            sb.AppendLine($"        var __r = {call};");
                            for (var i = 0; i < f.TupleTypes.Count; i++)
                            {
                                var t = f.TupleTypes[i]; var item = "__r.Item" + (i + 1);
                                switch (t)
                                {
                                    case "int": case "long": sb.AppendLine($"        Lua54.lua_pushinteger(L, {item});"); break;
                                    case "double": sb.AppendLine($"        Lua54.lua_pushnumber(L, {item});"); break;
                                    case "bool": sb.AppendLine($"        Lua54.lua_pushboolean(L, {item} ? 1 : 0);"); break;
                                    case "string": sb.AppendLine($"        LuaPush.Str(L, {item});"); break;
                                    default: sb.AppendLine($"        #error unsupported tuple element {t}"); break;
                                }
                            }
                            sb.AppendLine($"        return {f.TupleTypes.Count};");
                            break;
                        case "LuaMulti":
                        case "Fiesta.Bot.Api.LuaMulti":
                            sb.AppendLine($"        return {call}.Push(L);");
                            break;
                        default: sb.AppendLine($"        #error unsupported return type {f.RetType} in {m.Class}.{f.Method}"); break;
                    }
                    sb.AppendLine("    }");
                }
                sb.AppendLine("    internal static void __Register(IntPtr L, LuaRegistrar r)");
                sb.AppendLine("    {");
                foreach (var f in m.Fns)
                {
                    sb.AppendLine($"        r.Fn(L, \"{f.LuaName}\", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&__lua_{f.Method});");
                    if (f.Alias.Length > 0) sb.AppendLine($"        r.Alias(\"{f.Alias}\", \"{f.LuaName}\");");
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
                spc.AddSource($"{m.Class}.LuaBindings.g.cs", sb.ToString());
            }

            // the registry + manifest
            var g = new StringBuilder();
            g.AppendLine("// <auto-generated/>");
            g.AppendLine("#nullable enable");
            g.AppendLine("using System;");
            g.AppendLine("namespace Fiesta.Bot.Api;");
            g.AppendLine("/// <summary>Generated: module order, registration and the API manifest.</summary>");
            g.AppendLine("public static partial class GeneratedApi");
            g.AppendLine("{");
            g.AppendLine("    /// <summary>Module Lua names in index order.</summary>");
            g.AppendLine($"    public static readonly string[] ModuleNames = new[] {{ {string.Join(", ", ordered.Select(m => Json(m.LuaName)))} }};");
            g.AppendLine("    /// <summary>Create every module for a bot, in index order.</summary>");
            g.AppendLine("    public static ApiModule[] Create(Fiesta.Bot.Agent.AgentContext ctx) => new ApiModule[]");
            g.AppendLine("    {");
            foreach (var m in ordered) g.AppendLine($"        new global::{m.Ns}.{m.Class}(ctx),");
            g.AppendLine("    };");
            g.AppendLine("    /// <summary>Register every module's functions (the registrar creates the module tables).</summary>");
            g.AppendLine("    public static void Register(IntPtr L, LuaRegistrar r)");
            g.AppendLine("    {");
            foreach (var m in ordered)
            {
                g.AppendLine($"        r.BeginModule(L, \"{m.LuaName}\");");
                g.AppendLine($"        global::{m.Ns}.{m.Class}.__Register(L, r);");
                g.AppendLine($"        r.EndModule(L, \"{m.LuaName}\");");
            }
            g.AppendLine("    }");
            var json = new StringBuilder();
            json.Append("{\"modules\":[");
            var firstM = true;
            foreach (var m in ordered)
            {
                if (!firstM) json.Append(','); firstM = false;
                json.Append("{\"name\":").Append(Json(m.LuaName)).Append(",\"doc\":").Append(Json(m.Doc)).Append(",\"functions\":[");
                var firstF = true;
                foreach (var f in m.Fns)
                {
                    if (!firstF) json.Append(','); firstF = false;
                    json.Append("{\"name\":").Append(Json(f.LuaName))
                        .Append(",\"params\":[").Append(string.Join(",", f.Params.Select(p => "{\"name\":" + Json(p.Name) + ",\"type\":" + Json(p.Type) + (p.Default != null ? ",\"default\":" + Json(p.Default.Trim('"')) : "") + "}"))).Append(']')
                        .Append(",\"returns\":").Append(Json(f.Returns))
                        .Append(",\"cost\":").Append(Json(f.Cost)).Append(",\"alloc\":").Append(Json(f.Alloc))
                        .Append(",\"alias\":").Append(f.Alias.Length > 0 ? Json(f.Alias) : "null")
                        .Append(",\"doc\":").Append(Json(f.Doc)).Append('}');
                }
                json.Append("]}");
            }
            json.Append("]}");
            g.AppendLine("    /// <summary>The API manifest (functions part) as JSON.</summary>");
            g.AppendLine($"    public const string ManifestJson = {SymbolDisplay.FormatLiteral(json.ToString(), true)};");
            g.AppendLine($"    /// <summary>Number of functions.</summary>");
            g.AppendLine($"    public const int FunctionCount = {ordered.Sum(m => m.Fns.Count)};");
            g.AppendLine("}");
            spc.AddSource("GeneratedApi.g.cs", g.ToString());
        }
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

        private static bool TickAssembly(Compilation c) => c.AssemblyName is "Fiesta.Bot.Agent" or "Fiesta.Bot.Api" or "Fiesta.Bot.Brain";

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (!TickAssembly(start.Compilation)) return;
                start.RegisterSyntaxNodeAction(a =>
                {
                    if (IsAllowed(a.Node)) return;
                    switch (a.Node)
                    {
                        case AwaitExpressionSyntax aw:
                            a.ReportDiagnostic(Diagnostic.Create(Rule, aw.GetLocation(), "await"));
                            break;
                        case MemberAccessExpressionSyntax ma:
                        {
                            var name = ma.Name.Identifier.Text;
                            var sym = a.SemanticModel.GetSymbolInfo(ma).Symbol;
                            var owner = sym?.ContainingType?.ToDisplayString() ?? "";
                            if ((name == "UtcNow" || name == "Now") && owner == "System.DateTime")
                                a.ReportDiagnostic(Diagnostic.Create(Rule, ma.GetLocation(), "DateTime." + name));
                            else if (name == "Sleep" && owner == "System.Threading.Thread")
                                a.ReportDiagnostic(Diagnostic.Create(Rule, ma.GetLocation(), "Thread.Sleep"));
                            else if ((name == "Wait" || name == "Result" || name == "GetResult") && (owner.StartsWith("System.Threading.Tasks.Task") || owner.StartsWith("System.Runtime.CompilerServices.TaskAwaiter") || owner.StartsWith("System.Threading.Tasks.ValueTask")))
                                a.ReportDiagnostic(Diagnostic.Create(Rule, ma.GetLocation(), owner + "." + name));
                            break;
                        }
                    }
                }, SyntaxKind.AwaitExpression, SyntaxKind.SimpleMemberAccessExpression);
            });
        }

        // off-tick code in tick assemblies (snapshots, the debugger publisher) opts out with [OffTick]
        private static bool IsAllowed(SyntaxNode n)
        {
            for (var p = n.Parent; p != null; p = p.Parent)
                if (p is MemberDeclarationSyntax md && md.AttributeLists.SelectMany(l => l.Attributes).Any(x => x.Name.ToString() is "OffTick" or "OffTickAttribute"))
                    return true;
            return false;
        }
    }

    /// <summary>FBOT002: per-tick allocation patterns (LINQ, capturing lambdas, params arrays) in methods marked
    /// [HotPath]; a warning, per the allocation guideline.</summary>
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
            context.RegisterSyntaxNodeAction(a =>
            {
                var m = (MethodDeclarationSyntax)a.Node;
                if (!m.AttributeLists.SelectMany(l => l.Attributes).Any(x => x.Name.ToString() is "HotPath" or "HotPathAttribute")) return;
                foreach (var n in m.DescendantNodes())
                {
                    if (n is InvocationExpressionSyntax inv && a.SemanticModel.GetSymbolInfo(inv).Symbol is IMethodSymbol ms
                        && ms.ContainingType?.ToDisplayString() == "System.Linq.Enumerable")
                        a.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "LINQ " + ms.Name));
                    else if (n is LambdaExpressionSyntax lam)
                    {
                        var df = a.SemanticModel.AnalyzeDataFlow(lam);
                        if (df != null && df.Succeeded && df.Captured.Length > 0)
                            a.ReportDiagnostic(Diagnostic.Create(Rule, lam.GetLocation(), "capturing lambda"));
                    }
                    else if (n is ObjectCreationExpressionSyntax oc && a.SemanticModel.GetTypeInfo(oc).Type is { IsReferenceType: true } t)
                        a.ReportDiagnostic(Diagnostic.Create(Rule, oc.GetLocation(), "new " + t.Name));
                }
            }, SyntaxKind.MethodDeclaration);
        }
    }
}
