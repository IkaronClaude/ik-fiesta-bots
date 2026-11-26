using System.Collections.Concurrent;
using System.Text;

namespace Fiesta.Bot.Services;

/// <summary>The one place a client file is found (walkability, map data, minimap art, icons, tables): the client's own
/// path rules, case-blind.
///   * A path named inside a client file (.ini / .shmd / a table cell) has backslashes as separators and leading '.' and
///     '\' the client skips (MHBasic.ini names ".resmap\..."): <see cref="Normalize"/>.
///   * A referenced file is looked for as given, under the client root, beside the file that names it (its base name,
///     then the relative path), then in the client's common folders.
///   * A map's files live in resmap/&lt;kind&gt;/&lt;folder&gt;/ (field, IDField, KDField, MHField, then any other resmap
///     folder), named &lt;folder&gt;.&lt;ext&gt; after the folder actually found; the folder comes from MapInfo's MapFolderName.
///   * Names are matched case-blind (the client is a Windows program; its tables and folders disagree on case, and the
///     data may sit on a case-sensitive file system). Folder listings are read once, on first use, never a deep scan.
/// Every miss is kept (with the candidates tried) and reported to <see cref="OnMiss"/>.</summary>
public sealed class ClientFiles
{
    /// <summary>The client's common folders a referenced file is also looked for in (its base name).</summary>
    public static readonly string[] CommonFolders = ["resmap/fieldTexture", "resmap/Texture", "resmap/nifs/Common", "reschar/Texture", "resmap/field"];
    /// <summary>The resmap kinds a map folder is looked for in first, in the client's order.</summary>
    public static readonly string[] MapKinds = ["field", "IDField", "KDField", "MHField"];

    private readonly string _root;
    // directory (full path, lower-case) -> its entries by lower-case name -> the real name; null for a missing directory
    private readonly ConcurrentDictionary<string, Dictionary<string, string>?> _listing = new();
    private readonly ConcurrentDictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string What, string[] Tried)> _misses = new();

    private static readonly ConcurrentDictionary<string, ClientFiles> _shared = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The shared resolver of a client root (one listing cache and one miss log for every consumer).</summary>
    public static ClientFiles For(string root) => _shared.GetOrAdd(Normalize(root, keepLeading: true).TrimEnd('/'), r => new ClientFiles(r));
    /// <summary>The shared resolver of the client root a ressystem folder belongs to.</summary>
    public static ClientFiles ForRessystem(string ressystemDir) => For(Path.GetDirectoryName(Normalize(ressystemDir, keepLeading: true).TrimEnd('/')) ?? ressystemDir);
    /// <summary>A table of a ressystem folder by name, case-blind; null when absent.</summary>
    public static ShnTable? LoadTable(string ressystemDir, string name)
    {
        var p = Normalize(ressystemDir, keepLeading: true).TrimEnd('/');
        var f = For(Path.GetDirectoryName(p) ?? p).Find(p, name.EndsWith(".shn", StringComparison.OrdinalIgnoreCase) ? name : name + ".shn");
        return f is not null && File.Exists(f) ? ShnTable.Load(f) : null;
    }

    private Dictionary<string, string>? _folders;
    /// <summary>A map's folder from MapInfo (MapFolderName; B_Rou for Rou), else the map name.</summary>
    public string MapFolder(string mapName)
    {
        _folders ??= LoadFolders();
        return _folders.TryGetValue(mapName, out var f) && !string.IsNullOrWhiteSpace(f) ? f : mapName;
    }
    private Dictionary<string, string> LoadFolders()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Table("MapInfo") is { } p)
        {
            var t = ShnTable.Load(p);
            if (t.Col("MapFolderName") >= 0)
                for (var r = 0; r < t.RowCount; r++) if (t.Str(r, "MapName") is { Length: > 0 } n) d.TryAdd(n, t.Str(r, "MapFolderName") ?? n);
        }
        return d;
    }

    /// <summary>Called once per distinct miss: what was asked, the candidates tried.</summary>
    public Action<string, string[]>? OnMiss { get; set; }

    /// <summary>Create over a client root (the folder holding ressystem, resmap, resmenu ...).</summary>
    public ClientFiles(string root) { _root = Normalize(root, keepLeading: true).TrimEnd('/'); }

    /// <summary>The client root.</summary>
    public string Root => _root;
    /// <summary>The misses so far (what, candidates tried).</summary>
    public IReadOnlyCollection<(string What, string[] Tried)> Misses => _misses;

    /// <summary>A path as the client reads it: '\' -> '/', leading '.' / '\' / '/' skipped (unless an absolute path is
    /// meant: <paramref name="keepLeading"/>).</summary>
    public static string Normalize(string path, bool keepLeading = false)
    {
        var p = path.Trim().Replace('\\', '/');
        if (!keepLeading) p = p.TrimStart('.', '/');
        return p;
    }

    /// <summary>Text of a client text file (.ini, .conf ...): the client's code page (cp949).</summary>
    public static string ReadText(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return File.ReadAllText(path, Encoding.GetEncoding(949));
    }

    // ---- case-blind lookup

    private Dictionary<string, string>? List(string dir) => _listing.GetOrAdd(dir.ToLowerInvariant(), _ =>
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Directory.EnumerateFileSystemEntries(dir)) d[Path.GetFileName(e)] = Path.GetFileName(e);
            return d;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    });

    /// <summary>The real path of base + relative, every segment matched case-blind; null when absent.</summary>
    public string? Find(string baseDir, string relative)
    {
        var cur = baseDir.TrimEnd('/');
        foreach (var seg in Normalize(relative).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (List(cur) is not { } entries || !entries.TryGetValue(seg, out var real)) return null;
            cur = cur + "/" + real;
        }
        return cur;
    }

    private string? FindFile(string baseDir, string relative) => Find(baseDir, relative) is { } p && File.Exists(p) ? p : null;
    private string? FindDir(string baseDir, string relative) => Find(baseDir, relative) is { } p && Directory.Exists(p) ? p : null;

    /// <summary>A file a client file names, by the client's order: as given, under the root, beside the naming file (its
    /// base name, then the path), then the common folders; null (and a recorded miss) when nowhere.</summary>
    public string? Resolve(string path, string? relativeTo = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = relativeTo is null ? path : path + "|" + relativeTo;
        return _resolved.GetOrAdd(key, _ =>
        {
            var tried = new List<string>();
            string? Try(string baseDir, string rel) { tried.Add(baseDir + "/" + rel); return FindFile(baseDir, rel); }
            var raw = Normalize(path, keepLeading: true);
            if (Path.IsPathRooted(raw) && File.Exists(raw)) return raw;
            var p = Normalize(path);
            var name = p[(p.LastIndexOf('/') + 1)..];
            if (Try(_root, p) is { } a) return a;
            if (relativeTo is not null)
            {
                var dir = Path.GetDirectoryName(Normalize(relativeTo, keepLeading: true))?.Replace('\\', '/') ?? "";
                if (Try(dir, name) is { } b) return b;
                if (Try(dir, p) is { } c) return c;
            }
            foreach (var d in CommonFolders)
                if (Try(_root + "/" + d, name) is { } e) return e;
            Miss(path, tried);
            return null;
        });
    }

    // ---- maps

    /// <summary>A map's folder (MapInfo MapFolderName) under resmap: field, IDField, KDField, MHField, then any other
    /// resmap folder; null (and a recorded miss) when absent.</summary>
    public string? MapDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        return _resolved.GetOrAdd("dir|" + folder, _ =>
        {
            var tried = new List<string>();
            var resmap = FindDir(_root, "resmap");
            if (resmap is null) { Miss("map folder " + folder, [_root + "/resmap"]); return null; }
            foreach (var kind in MapKinds)
            {
                tried.Add($"{resmap}/{kind}/{folder}");
                if (FindDir(resmap, kind + "/" + folder) is { } d) return d;
            }
            foreach (var kind in List(resmap)?.Values.ToArray() ?? [])
            {
                if (MapKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) continue;
                tried.Add($"{resmap}/{kind}/{folder}");
                if (FindDir(resmap, kind + "/" + folder) is { } d) return d;
            }
            Miss("map folder " + folder, tried);
            return null;
        });
    }

    /// <summary>A map file: &lt;folder&gt;.&lt;ext&gt; in the map's folder, named after the folder actually found (ext with
    /// its dot: ".shbd"); null (and a recorded miss) when absent.</summary>
    public string? MapFile(string folder, string ext)
    {
        if (MapDir(folder) is not { } dir) return null;
        var found = dir[(dir.LastIndexOf('/') + 1)..];
        return _resolved.GetOrAdd("map|" + folder + "|" + ext, _ =>
        {
            if (FindFile(dir, found + ext) is { } f) return f;
            Miss($"{folder}{ext}", [dir + "/" + found + ext]);
            return null;
        });
    }

    // ---- menu art

    /// <summary>An icon sheet: resmenu/Icon/&lt;file&gt;.dds by the client's rules (null and a miss when absent).</summary>
    public string? Icon(string iconFile) => string.IsNullOrWhiteSpace(iconFile) || iconFile.Trim() == "-" ? null
        : Resolve("resmenu/Icon/" + WithExt(iconFile, ".dds"));
    /// <summary>A minimap image: resmenu/minimap/&lt;name&gt;.dds (null and a miss when absent).</summary>
    public string? Minimap(string name) => string.IsNullOrWhiteSpace(name) ? null : Resolve("resmenu/minimap/" + WithExt(name, ".dds"));
    /// <summary>A table in ressystem (null and a miss when absent).</summary>
    public string? Table(string name) => Resolve("ressystem/" + WithExt(name, ".shn"));

    private static string WithExt(string name, string ext) => Path.HasExtension(name) ? name : name + ext;

    private void Miss(string what, List<string> tried)
    {
        _misses.Enqueue((what, tried.ToArray()));
        OnMiss?.Invoke(what, tried.ToArray());
    }
}
