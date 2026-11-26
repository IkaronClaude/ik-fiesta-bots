using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fiesta.Bot.Fleet;
using Fiesta.Bot.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Fiesta.Bot.Server;

/// <summary>Static data for the game view (API_SPEC 14.2): names, cooldown lengths, icons cut from the client's
/// atlases, minimap art and placement. Everything is read from the configured client directory and cached.</summary>
public sealed class GameStatic
{
    private readonly HostServices _h;
    private readonly Lazy<ShnTable?> _itemView, _skillView, _abView, _abState, _mapView;
    private readonly ClientFiles _files;
    private readonly Lazy<Dictionary<int, int>> _abRowByWire;
    private readonly Lazy<HashSet<int>> _productionIds;
    private readonly ConcurrentDictionary<(char, int), byte[]?> _icons = new();
    private readonly ConcurrentDictionary<int, byte[]?> _minimaps = new();

    /// <summary>Bumped whenever these routes change what they return for the same client data (busts browser caches).</summary>
    public const int OutputVersion = 4;

    /// <summary>Client-data fingerprint (part of every ETag).</summary>
    public string Fingerprint { get; }

    /// <summary>Create.</summary>
    public GameStatic(HostServices h)
    {
        _h = h;
        ShnTable? T(string n) => ClientFiles.LoadTable(h.RessystemDir, n);
        _files = ClientFiles.For(h.ClientRoot);
        _itemView = new(() => T("ItemViewInfo"));
        _skillView = new(() => T("ActiveSkillView"));
        _abView = new(() => T("AbStateView"));
        _abState = new(() => T("AbState"));
        _mapView = new(() => T("MapViewInfo"));
        _abRowByWire = new(() =>
        {
            var d = new Dictionary<int, int>();
            if (_abState.Value is { } t) for (var r = 0; r < t.RowCount; r++) d.TryAdd((int)t.Num(r, "AbStataIndex"), r);
            return d;
        });
        // production skills: the ids the client's own production tables name (recipes in Produce, gathering in Gather)
        _productionIds = new(() =>
        {
            var set = new HashSet<int>();
            if (T("Produce") is { } p) for (var r = 0; r < p.RowCount; r++) set.Add((int)p.Num(r, "ProductID"));
            if (T("Gather") is { } g) for (var r = 0; r < g.RowCount; r++) set.Add((int)g.Num(r, "GatherID"));
            return set;
        });
        var sb = new StringBuilder(Path.GetFullPath(h.RessystemDir)).Append('|').Append(OutputVersion);
        foreach (var f in new[] { "ItemInfo.shn", "ActiveSkill.shn", "MobInfo.shn", "MapInfo.shn", "ItemViewInfo.shn" })
        {
            var p = Path.Combine(h.RessystemDir, f);
            if (File.Exists(p)) sb.Append('|').Append(new FileInfo(p).Length).Append('|').Append(File.GetLastWriteTimeUtc(p).Ticks);
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }


    private static int RowOf(ShnTable? t, string col, long id)
    {
        if (t is null) return -1;
        var c = t.Col(col);
        for (var r = 0; r < t.RowCount; r++) if (t.Num(r, c) == id) return r;
        return -1;
    }

    private (string File, int Index)? ItemIcon(int id)
    {
        var t = _itemView.Value; var r = RowOf(t, "ID", id);
        if (r < 0) return null;
        var f = t!.Str(r, "IconFile");
        return string.IsNullOrEmpty(f) || f == "-" ? null : (f, (int)t.Num(r, "IconIndex"));
    }

    private (string File, int Index)? SkillIcon(int id)
    {
        var t = _skillView.Value; var r = RowOf(t, "ID", id);
        if (r < 0) return null;
        var f = t!.Str(r, "IconFile");
        return string.IsNullOrEmpty(f) || f == "-" ? null : (f, (int)t.Num(r, "IconIndex"));
    }

    /// <summary>The AbStateView row of a wire abstate index (joined through AbState by internal name).</summary>
    private int AbViewRow(int wire)
    {
        if (_abState.Value is not { } st || !_abRowByWire.Value.TryGetValue(wire, out var r)) return -1;
        var inx = st.Str(r, "InxName");
        if (_abView.Value is not { } v || inx is null) return -1;
        var c = v.Col("inxName");
        for (var i = 0; i < v.RowCount; i++) if (string.Equals(v.Str(i, c), inx, StringComparison.Ordinal)) return i;
        return -1;
    }

    private (string File, int Index)? AbIcon(int wire)
    {
        var r = AbViewRow(wire);
        if (r < 0) return null;
        var f = _abView.Value!.Str(r, "iconFile");
        return string.IsNullOrEmpty(f) || f == "-" ? null : (f, (int)_abView.Value.Num(r, "icon"));
    }

    /// <summary>An icon PNG ('i' item, 's' skill, 'a' abstate), or null.</summary>
    public byte[]? Icon(char kind, int id) => _icons.GetOrAdd((kind, id), k =>
    {
        var cell = k.Item1 switch { 'i' => ItemIcon(k.Item2), 's' => SkillIcon(k.Item2), _ => AbIcon(k.Item2) };
        if (cell is not { } c || _files.Icon(c.File) is not { } path) return null;
        try { return ClientImages.AtlasCellPng(path, c.Index); } catch (IOException) { return null; }
    });

    /// <summary>A map's minimap art file and whether the game shows a minimap there at all, from MapViewInfo: the art is
    /// named by its MapFolderName (PsyInDn02 draws PsyIn.dds; Rou / Eld use B_Rou / B_Eld), falling back to the map name;
    /// MinimapView 0 means the game itself shows none (the job-change instances, Tower, QField, Wedding ...).</summary>
    private (string? Path, bool Shown) MinimapFile(MapInfoRow m)
    {
        var shown = true; string? folder = null;
        if (_mapView.Value is { } mv && FindMapView(mv, m.MapName) is var r and >= 0)
        {
            folder = mv.Str(r, "MapFolderName");
            if (mv.Col("MinimapView") >= 0) shown = mv.Num(r, "MinimapView") != 0;
        }
        foreach (var name in new[] { folder, m.MapName })
            if (!string.IsNullOrEmpty(name) && _files.Minimap(name) is { } p) return (p, shown);
        return (null, shown);
    }

    /// <summary>A map's minimap PNG, or null when the client has no art for it (served even where the game hides its
    /// minimap: the console still draws it).</summary>
    public byte[]? Minimap(int mapId) => _minimaps.GetOrAdd(mapId, id =>
    {
        if (_h.Data.Map(id) is not { } m || MinimapFile(m) is not { Path: { } path }) return null;
        try { return ClientImages.ImagePng(path); } catch (IOException) { return null; }
    });

    /// <summary>Item.</summary>
    public object? Item(int id) => _h.Data.Item(id) is not { } r ? null : new
    {
        id, inx = r.InxName, name = r.Name, type = r.Type, @class = r.ItemClass, equip = r.Equip, demandLv = r.DemandLevel,
        maxLot = r.MaxLot, buyPrice = r.BuyPrice, sellPrice = r.SellPrice, icon = ItemIcon(id) is { } c && _files.Icon(c.File) is not null,
    };

    /// <summary>Skill.</summary>
    public object? Skill(int id)
    {
        if (_h.Data.Skill(id) is not { } r) return null;
        var vr = RowOf(_skillView.Value, "ID", id);
        string? desc = null;
        if (vr >= 0 && _skillView.Value!.Col("Descript") >= 0) desc = _skillView.Value.Str(vr, "Descript");
        return new
        {
            id, inx = r.InxName, name = r.Name, category = SkillCategory(id, r.InxName), rowKey = r.Family, step = r.Step, maxStep = r.MaxStep, sp = r.Sp, hp = r.Hp, cooldownMs = r.DelayMs,
            groupCooldownMs = r.DelayGroup > 0 ? r.DelayGroupMs : 0, range = r.Range, castMs = r.CastTimeMs, description = desc,
            icon = SkillIcon(id) is { } c && _files.Icon(c.File) is not null,
        };
    }

    // The game client's skill window (2016 client): Skill::IsProduceSkill (0x69EE30) treats ids 20000..29998 as produce
    // skills, which never appear on the active page; SkillWin::ResetContents (0x591280) lists the learned ActiveSkill rows
    // outside that range except SummonMate01. These mirror client code, so the constants stand as client facts.
    private const int ProduceFirst = 20000, ProduceLast = 29998;
    private const string HiddenActive = "SummonMate01";

    /// <summary>active / production / other (hidden in the client's skill window).</summary>
    public string SkillCategory(int id, string inx)
    {
        if (id is >= ProduceFirst and <= ProduceLast)
            // GUESS: which produce-range skills the production page lists is not confirmed from the client yet; the ids the
            // client's own production tables name (Produce recipes, Gather) are taken as the listed ones
            return _productionIds.Value.Contains(id) ? "production" : "other";
        return inx == HiddenActive ? "other" : "active";
    }

    /// <summary>Abstate by wire index.</summary>
    public object? AbState(int wire)
    {
        if (_abState.Value is not { } st || !_abRowByWire.Value.TryGetValue(wire, out var r)) return null;
        var vr = AbViewRow(wire);
        var v = _abView.Value;
        return new
        {
            id = wire, inx = st.Str(r, "InxName"), name = vr >= 0 ? v!.Str(vr, "Descript") : st.Str(r, "InxName"),
            description = vr >= 0 ? v!.Str(vr, "Descript") : null, sort = vr >= 0 ? v!.Str(vr, "IconSort") : null,
            icon = AbIcon(wire) is { } c && _files.Icon(c.File) is not null,
        };
    }

    /// <summary>Mob.</summary>
    public object? Mob(int id) => _h.Data.Mob(id) is not { } r ? null
        : new { id, inx = r.InxName, name = r.Name, level = r.Level, maxHp = r.MaxHp, npc = r.IsNpc, playerSide = r.IsPlayerSide };

    /// <summary>Class.</summary>
    public object? Class(int id) => _h.Data.ClassName(id) is { Length: > 0 } n ? new { id, name = n } : null;

    /// <summary>Map; <paramref name="complete"/> is false when its world size is unknown (never cache that).</summary>
    public object? Map(int id, out bool complete)
    {
        complete = true;
        if (_h.Data.Map(id) is not { } m) return null;
        // the world size comes from the block file's header (read at once, kept), not from a walkability grid a bot happens
        // to have loaded: the record is complete from the first request (it used to serve world:null for a day)
        var size = _sizes.GetOrAdd(m.MapName, n => BlockLayer.ReadSize(_h.ClientRoot, n));
        complete = size is not null;
        double? ww = size is { } sz ? sz.W * BlockLayer.WorldPerTile : null, wh = size is { } sz2 ? sz2.H * BlockLayer.WorldPerTile : null;
        object? cover = null;
        var vr = _mapView.Value is { } mv ? FindMapView(mv, m.MapName) : -1;
        if (vr >= 0 && ww is { } w && wh is { } h)
        {
            var mv2 = _mapView.Value!;
            const double norm = 512.0;
            double sx = mv2.Num(vr, "StartX"), sy = mv2.Num(vr, "StartY"), ex = mv2.Num(vr, "EndX") + 1, ey = mv2.Num(vr, "EndY") + 1;
            if (ex > sx && ey > sy) cover = new { x0 = sx / norm * w, y0 = (1 - ey / norm) * h, x1 = ex / norm * w, y1 = (1 - sy / norm) * h };
        }
        return new
        {
            id, mapName = m.MapName, name = m.Name, minimap = MinimapFile(m) is { Path: not null },
            minimapShown = MinimapFile(m).Shown,
            world = ww is null ? null : new { w = ww, h = wh }, cover,
        };
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int W, int H)?> _sizes = new(StringComparer.OrdinalIgnoreCase);

    private static int FindMapView(ShnTable t, string mapName)
    {
        var c = t.Col("MapName");
        for (var r = 0; r < t.RowCount; r++) if (string.Equals(t.Str(r, c), mapName, StringComparison.OrdinalIgnoreCase)) return r;
        return -1;
    }

    /// <summary>Counts for the index route.</summary>
    public object Index() => new
    {
        fingerprint = Fingerprint, items = _itemView.Value?.RowCount ?? 0, skills = _skillView.Value?.RowCount ?? 0,
        abstates = _abState.Value?.RowCount ?? 0, mapViews = _mapView.Value?.RowCount ?? 0,
    };
}

/// <summary>The static routes of API_SPEC 14.2.</summary>
public static class GameStaticEndpoints
{
    private const string CacheControl = "public, max-age=86400";

    private static IResult Cached(HttpContext ctx, GameStatic s, string key, Func<IResult?> make, bool cache = true)
    {
        var etag = $"\"{s.Fingerprint}-{key}\"";
        if (cache && ctx.Request.Headers.IfNoneMatch.Contains(etag)) return Results.StatusCode(304);
        var r = make();
        if (r is null) return Results.NotFound(new { type = "about:blank", title = $"no {key}", status = 404 });
        if (cache) { ctx.Response.Headers.ETag = etag; ctx.Response.Headers.CacheControl = CacheControl; }
        else ctx.Response.Headers.CacheControl = "no-store";
        return r;
    }

    private static Func<int, object?> Pick(GameStatic s, string kind) => kind switch
    {
        "items" => s.Item, "skills" => s.Skill, "abstates" => s.AbState, "mobs" => s.Mob, "classes" => s.Class, _ => _ => null,
    };

    /// <summary>Map the routes.</summary>
    public static RouteGroupBuilder MapGameStaticEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/static", (HttpContext ctx, GameStatic s) => Cached(ctx, s, "index", () => Results.Json(s.Index(), Http.Json), cache: false));
        api.MapGet("/static/maps/{id:int}", (int id, HttpContext ctx, GameStatic s) =>
        {
            var m = s.Map(id, out var complete);
            return Cached(ctx, s, $"map-{id}", () => m is null ? null : Results.Json(m, Http.Json), complete);
        });
        api.MapGet("/static/maps/{id:int}/minimap.png", (int id, HttpContext ctx, GameStatic s) =>
            Cached(ctx, s, $"minimap-{id}", () => s.Minimap(id) is { } png ? Results.File(png, "image/png") : null));
        api.MapGet("/static/{kind}/{id:int}", (string kind, int id, HttpContext ctx, GameStatic s) =>
            Cached(ctx, s, $"{kind}-{id}", () => Pick(s, kind)(id) is { } o ? Results.Json(o, Http.Json) : null));
        api.MapGet("/static/{kind}", (string kind, string? ids, HttpContext ctx, GameStatic s) =>
        {
            var f = Pick(s, kind);
            var d = new Dictionary<string, object>();
            foreach (var part in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(500))
                if (int.TryParse(part, out var id) && f(id) is { } o) d[part] = o;
            ctx.Response.Headers.CacheControl = CacheControl;
            return Results.Json(d, Http.Json);
        });
        api.MapGet("/icons/{kind}/{file}", (string kind, string file, HttpContext ctx, GameStatic s) =>
        {
            var k = kind switch { "item" => 'i', "skill" => 's', "abstate" => 'a', _ => '?' };
            if (k == '?' || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !int.TryParse(file[..^4], out var id))
                return Results.NotFound();
            return Cached(ctx, s, $"icon-{k}-{id}", () => s.Icon(k, id) is { } png ? Results.File(png, "image/png") : null);
        });
        return api;
    }
}
