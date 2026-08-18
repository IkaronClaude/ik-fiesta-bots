using System.Reflection;

namespace Fiesta.Bot.Host;

/// <summary>The live BOT WATCH page — "a window into everything going on with the bot; like a stat panel, you look at it a…</summary>
internal static class WatchPage
{
    private const string ResourceName = "Fiesta.Bot.Host.Pages.watch.html";

    public static string Html { get; } = Load();

    private static string Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(ResourceName);
        if (s is null)
        {
            // Fail LOUDLY and usefully: a mis-set resource name is otherwise a blank page with no clue why
            var have = string.Join(", ", asm.GetManifestResourceNames());
            return $"<pre>watch.html resource '{ResourceName}' not found.\nEmbedded resources: {have}</pre>";
        }
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
