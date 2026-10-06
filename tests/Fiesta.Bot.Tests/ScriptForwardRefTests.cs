using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>
/// A Lua `local function f()` is only in scope BELOW its definition. A function defined above it that calls `f(...)`
/// resolves a GLOBAL f, which is nil at run time: "attempt to call a nil value" on the first tick that reaches the line.
/// Compiling does not catch it (2026-10-05/06: myOrder, knownKind, rawsBuyable, tonce - four such stalls in one day,
/// each a bot doing nothing until the next check). This lint flags a call to a later `local function` from an earlier
/// line unless the name was forward-declared (`local f` / `local a, f`) above the call.
/// </summary>
public class ScriptForwardRefTests
{
    private static string ScriptsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var s = Path.Combine(d.FullName, "scripts");
            if (Directory.Exists(s) && File.Exists(Path.Combine(s, "level_quest.lua"))) return s;
        }
        throw new DirectoryNotFoundException("scripts/ not found above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string> Scripts()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(ScriptsDir(), "*.lua").OrderBy(f => f)) data.Add(Path.GetFileName(f));
        return data;
    }

    private static readonly Regex LocalFunc = new(@"^\s*local\s+function\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex LocalDecl = new(@"^\s*local\s+([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*(?:=|$|--)", RegexOptions.Compiled);

    private static string StripCommentsAndStrings(string line)
    {
        // good enough for a lint: drop string literals and the trailing comment
        var s = Regex.Replace(line, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");
        s = Regex.Replace(s, "'(?:\\\\.|[^'\\\\])*'", "''");
        var c = s.IndexOf("--", StringComparison.Ordinal);
        return c >= 0 ? s[..c] : s;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void No_call_to_a_later_local_function_without_a_forward_declaration(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(ScriptsDir(), name));
        var code = lines.Select(StripCommentsAndStrings).ToArray();
        // definition line of every `local function NAME`
        var defs = new Dictionary<string, int>();
        for (var i = 0; i < code.Length; i++)
        {
            var m = LocalFunc.Match(code[i]);
            if (m.Success && !defs.ContainsKey(m.Groups[1].Value)) defs[m.Groups[1].Value] = i;
        }
        // first forward declaration line of each name (`local x` / `local a, x = ...` not `local function`)
        var fwd = new Dictionary<string, int>();
        for (var i = 0; i < code.Length; i++)
        {
            if (LocalFunc.IsMatch(code[i])) continue;
            var m = LocalDecl.Match(code[i]);
            if (!m.Success) continue;
            foreach (var n in m.Groups[1].Value.Split(',').Select(x => x.Trim()))
                if (!fwd.ContainsKey(n)) fwd[n] = i;
        }
        var problems = new List<string>();
        foreach (var (fn, defLine) in defs)
        {
            if (fwd.TryGetValue(fn, out var fl) && fl < defLine) continue;   // forward-declared above: fine
            var call = new Regex(@"(?<![\w.:])" + Regex.Escape(fn) + @"\s*\(", RegexOptions.Compiled);
            for (var i = 0; i < defLine; i++)
            {
                if (call.IsMatch(code[i]) && !LocalFunc.IsMatch(code[i]))
                {
                    problems.Add($"{name}:{i + 1}: calls `{fn}(` but `local function {fn}` is defined at line {defLine + 1} - it is NIL here (forward-declare `local {fn}` above, or move the definition up)");
                    break;
                }
            }
        }
        if (problems.Count > 0) throw new Xunit.Sdk.XunitException(string.Join("\n", problems));
    }
}
