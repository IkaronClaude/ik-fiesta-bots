using System.Reflection;

namespace Fiesta.Bot.Host;

/// <summary>The live BOT WATCH page — "a window into everything going on with the bot; like a stat panel,
/// you look at it and immediately know where the bot is" (operator epic, 2026-08-05). Served at "/watch".
/// <para>Shows live vitals, the metric windows, a browser-rendered position heatmap, and the log with a
/// severity filter, all polling the existing endpoints. Dependency-free vanilla JS so it works under any
/// CSP and offline.</para>
/// <para>The heatmap is drawn CLIENT-SIDE from raw timestamp+map+coord points (operator's preference): the
/// server never rasterises, decay is applied in the browser from each point's age, and polling with `since`
/// means the live view costs a few numbers per second instead of an image.</para>
/// <para><b>Loaded from an EMBEDDED RESOURCE</b> (<c>Pages/watch.html</c>) — not a C# string literal, not
/// wwwroot. The page stays a real <c>.html</c> file, so editor tooling, formatting and diffs all work and no
/// <c>"""</c> sequence in the markup can collide with a raw-string delimiter; and it is still compiled into
/// the assembly, so it ships inside the image with no publish/copy step and no static-file middleware (this
/// host sets <c>StaticWebAssetsEnabled=false</c>). <see cref="StatusPage"/> predates this and still uses a
/// string literal — it should move too (tracked as a P3).</para>
/// <para>Read once and cached: the resource cannot change at runtime, and this page is polled every
/// second.</para></summary>
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
            // Fail LOUDLY and usefully: a mis-set resource name is otherwise a blank page with no clue why.
            // Listing what IS embedded turns "why is /watch empty" into a one-look answer.
            var have = string.Join(", ", asm.GetManifestResourceNames());
            return $"<pre>watch.html resource '{ResourceName}' not found.\nEmbedded resources: {have}</pre>";
        }
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
