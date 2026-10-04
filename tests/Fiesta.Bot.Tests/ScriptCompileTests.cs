using System;
using System.IO;
using System.Linq;
using MoonSharp.Interpreter;
using Shouldly;
using Xunit;

namespace Fiesta.Bot.Tests;

/// <summary>
/// Every driver script compiles under the engine the host runs (MoonSharp) - a syntax error, or one local too many in
/// level_quest's huge main function, otherwise only shows up after a deploy, as a bot that stops doing anything.
/// Compiling does not run the script, so no bot API is needed.
/// </summary>
public class ScriptCompileTests
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

    [Theory]
    [MemberData(nameof(Scripts))]
    public void The_script_compiles(string name)
    {
        var source = File.ReadAllText(Path.Combine(ScriptsDir(), name));
        try { new Script().LoadString(source, null, name); }
        catch (InterpreterException ex) { throw new Xunit.Sdk.XunitException($"{name}: {ex.DecoratedMessage ?? ex.Message}"); }
    }
}
