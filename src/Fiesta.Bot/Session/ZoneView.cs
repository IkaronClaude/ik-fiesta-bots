using System.Collections.Concurrent;
using Fiesta.Bot.Behaviors;
using FiestaLibReloaded.Networking;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Session;

/// <summary>A player the bot can currently see in zone (from Briefinfo broadcasts).</summary>
public sealed record NearbyPlayer(ushort Handle, string Name, byte Class, byte Level, uint X, uint Y)
{
    public DateTime SeenAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>An in-zone chat line overheard from a nearby speaker.</summary>
public sealed record ChatMessage(ushort Handle, string? SenderName, string Text)
{
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A live, read-only model of what one bot perceives in its zone, built by
/// decoding the inbound frames a <see cref="BotSession"/> fans out. Tracks nearby
/// players (appear/leave from Briefinfo) and raises chat events — the shared
/// perception layer the buff/party behaviors and the future LLM-control API both
/// consume. Decoding runs on the session read loop, so it stays cheap; reactions
/// are fired as events for handlers to offload.
/// </summary>
public sealed class ZoneView : IDisposable
{
    private static readonly ushort OpBriefChar = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_CHARACTER_CMD>();
    private static readonly ushort OpBriefLogin = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD>();
    private static readonly ushort OpBriefDelete = PacketRegistry.GetOpcode<PROTO_NC_BRIEFINFO_BRIEFINFODELETE_CMD>();
    private static readonly ushort OpClientItem = PacketRegistry.GetOpcode<PROTO_NC_CHAR_CLIENT_ITEM_CMD>();
    private static readonly ushort OpCellChange = PacketRegistry.GetOpcode<PROTO_NC_ITEM_CELLCHANGE_CMD>();
    private static readonly ushort OpEquipChange = PacketRegistry.GetOpcode<PROTO_NC_ITEM_EQUIPCHANGE_CMD>();

    private readonly BotSession _session;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<ushort, NearbyPlayer> _nearby = new();
    private readonly ConcurrentDictionary<byte, ushort> _inventory = new(); // bag slot -> itemId
    private readonly ConcurrentDictionary<byte, ushort> _equipment = new(); // equip slot -> itemId

    public ZoneView(BotSession session, Action<string>? log = null)
    {
        _session = session;
        _log = log;
        _session.PacketReceived += OnPacket;
    }

    /// <summary>Raised when a player enters (or refreshes in) view.</summary>
    public event Action<NearbyPlayer>? PlayerAppeared;

    /// <summary>Raised when a tracked handle leaves view.</summary>
    public event Action<ushort>? PlayerLeft;

    /// <summary>Raised for every overheard nearby chat line.</summary>
    public event Action<ChatMessage>? ChatReceived;

    public IReadOnlyCollection<NearbyPlayer> NearbyPlayers => _nearby.Values.ToArray();
    public int NearbyCount => _nearby.Count;
    public ChatMessage? LastChat { get; private set; }

    /// <summary>Current bag contents: slot → itemId (built from the login item list
    /// and live cell/equip changes).</summary>
    public IReadOnlyDictionary<byte, ushort> Inventory => _inventory;

    /// <summary>Currently worn gear: equip slot → itemId (from equip-change events).</summary>
    public IReadOnlyDictionary<byte, ushort> Equipment => _equipment;

    public bool TryGetPlayer(ushort handle, out NearbyPlayer player) => _nearby.TryGetValue(handle, out player!);

    private void OnPacket(FiestaPacket pkt)
    {
        var op = pkt.Opcode;
        if (op == OpBriefChar)
        {
            foreach (var c in pkt.ReadBody<PROTO_NC_BRIEFINFO_CHARACTER_CMD>().chars)
                AddOrUpdate(c);
        }
        else if (op == OpBriefLogin)
        {
            AddOrUpdate(pkt.ReadBody<PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD>());
        }
        else if (op == OpBriefDelete)
        {
            var hnd = pkt.ReadBody<PROTO_NC_BRIEFINFO_BRIEFINFODELETE_CMD>().hnd;
            if (_nearby.TryRemove(hnd, out var gone))
            {
                _log?.Invoke($"[ZoneView] player left: {gone.Name} (h={hnd})");
                PlayerLeft?.Invoke(hnd);
            }
        }
        else if (op == OpClientItem)
        {
            // Full bag snapshot at login (one frame per box). Decode typed; tolerate
            // any struct quirk so a bad frame never kills the read loop.
            try
            {
                foreach (var it in pkt.ReadBody<PROTO_NC_CHAR_CLIENT_ITEM_CMD>().ItemArray)
                {
                    var slot = (byte)(it.location.Inven & 0xFF);
                    if (it.info.itemid != 0) _inventory[slot] = it.info.itemid;
                }
            }
            catch { /* skip unparseable inventory frame */ }
        }
        else if (op == OpCellChange)
        {
            // [exchange:2][location:2][itemid:2][attr…] — a slot gained/changed an item.
            var p = pkt.Payload.Span;
            if (p.Length >= 6)
            {
                var slot = p[2];
                var itemId = (ushort)(p[4] | (p[5] << 8));
                if (itemId != 0) _inventory[slot] = itemId; else _inventory.TryRemove(slot, out _);
            }
        }
        else if (op == OpEquipChange)
        {
            // [exchange:2][location:1][itemid:2…] — item moved bag→equip slot.
            var p = pkt.Payload.Span;
            if (p.Length >= 1) _inventory.TryRemove(p[0], out _);   // vacate bag slot
            if (p.Length >= 5)
            {
                var equipSlot = p[2];
                var itemId = (ushort)(p[3] | (p[4] << 8));
                if (itemId != 0) _equipment[equipSlot] = itemId; else _equipment.TryRemove(equipSlot, out _);
            }
        }
        else if (op == ChatCodec.SomeoneChatOpcode)
        {
            if (ChatCodec.TryDecodeSomeoneChat(pkt.Payload.Span, out var handle, out var text)
                && text.Length > 0)
            {
                var name = _nearby.TryGetValue(handle, out var p) ? p.Name : null;
                var msg = new ChatMessage(handle, name, text);
                LastChat = msg;
                _log?.Invoke($"[ZoneView] chat <{name ?? $"h{handle}"}>: {text}");
                ChatReceived?.Invoke(msg);
            }
        }
    }

    private void AddOrUpdate(PROTO_NC_BRIEFINFO_LOGINCHARACTER_CMD c)
    {
        var name = FiestaText.Decode(c.charid.n5_name);
        var player = new NearbyPlayer(c.handle, name, c.chrclass, c.Level, c.coord.xy.x, c.coord.xy.y);
        var isNew = !_nearby.ContainsKey(c.handle);
        _nearby[c.handle] = player;
        if (isNew)
        {
            _log?.Invoke($"[ZoneView] player appeared: {name} (h={c.handle} class={c.chrclass} lvl={c.Level})");
            PlayerAppeared?.Invoke(player);
        }
    }

    public void Dispose() => _session.PacketReceived -= OnPacket;
}
