using System.Collections.Concurrent;
using Fiesta.Bot.Session;
using FiestaLibReloaded.Networking.Structs;

namespace Fiesta.Bot.Behaviors;

/// <summary>
/// A priest parked in town that buffs people. It reacts to the <see cref="ZoneView"/>
/// perception layer: when someone nearby chats the trigger word ("buff pls"), it
/// casts the configured buff skills on that speaker; optionally it also buffs
/// players as they appear. Buffs are single-target object casts —
/// <see cref="PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ"/> {skill, target=zone handle}.
///
/// Casting is offloaded off the session read loop, per-target throttled, and a
/// no-op (logged) when no buff skills are configured/learnt yet. This same
/// request→react seam is what an LLM controller will drive later.
/// </summary>
public sealed class BuffInTownBehavior : IDisposable
{
    private readonly BotSession _session;
    private readonly ZoneView _view;
    private readonly BuffConfig _config;
    private readonly Action<string> _log;
    private readonly CancellationToken _ct;
    private readonly ConcurrentDictionary<ushort, DateTime> _lastBuffedUtc = new();

    public BuffInTownBehavior(BotSession session, ZoneView view, BuffConfig config,
        Action<string> log, CancellationToken ct)
    {
        _session = session;
        _view = view;
        _config = config;
        _log = log;
        _ct = ct;
        _view.ChatReceived += OnChat;
        if (_config.AutoBuffNearby) _view.PlayerAppeared += OnAppeared;
        _log($"[Buff] active — trigger='{_config.Trigger}', skills=[{string.Join(",", _config.SkillIds)}]"
             + (_config.AutoBuffNearby ? ", auto-buff-nearby" : ""));
    }

    private void OnChat(ChatMessage msg)
    {
        if (msg.Text.IndexOf(_config.Trigger, StringComparison.OrdinalIgnoreCase) < 0) return;
        var who = msg.SenderName ?? $"h{msg.Handle}";
        _log($"[Buff] request from {who}: \"{msg.Text}\"");
        _ = BuffTargetAsync(msg.Handle, who);
    }

    private void OnAppeared(NearbyPlayer p) => _ = BuffTargetAsync(p.Handle, p.Name);

    private async Task BuffTargetAsync(ushort target, string who)
    {
        // Per-target cooldown so we don't spam the same person.
        var now = DateTime.UtcNow;
        if (_lastBuffedUtc.TryGetValue(target, out var last) && now - last < _config.TargetCooldown)
            return;
        _lastBuffedUtc[target] = now;

        if (_config.SkillIds.Count == 0)
        {
            _log($"[Buff] would buff {who} (h={target}) but no buff skills are configured/learnt — skipping");
            return;
        }

        try
        {
            foreach (var skill in _config.SkillIds)
            {
                _ct.ThrowIfCancellationRequested();
                var cast = new PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ { skill = skill, target = target };
                await _session.SendAsync(cast, _ct);
                _log($"[Buff] >> cast skill {skill} on {who} (h={target})");
                if (_config.CastInterval > TimeSpan.Zero)
                    await Task.Delay(_config.CastInterval, _ct);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex)
        {
            _log($"[Buff] cast on {who} (h={target}) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _view.ChatReceived -= OnChat;
        _view.PlayerAppeared -= OnAppeared;
    }
}
