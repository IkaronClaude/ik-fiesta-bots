using System.Buffers.Binary;
using Fiesta.Bot.Net;

namespace Fiesta.Bot.Protocol;

/// <summary>The I/O side of one bot in the game: the zone link (map login, the per-frame decode into the bot's
/// inbound ring, heartbeats, in-band map links and cross-server handoffs, logout) plus the world manager link kept
/// alive beside it. Everything here runs on I/O threads; the tick only sees decoded events and only writes intents.
/// Protocol-level obligations that need no decision (heartbeat answers, MAP_LOGINCOMPLETE after a map link, the
/// reconnect of a cross-server handoff) are handled here, never by the brain.</summary>
public sealed class ZoneSession : IFrameSink, IAsyncDisposable
{
    private readonly WireProfile _wire;
    private readonly DecoderTable _table;
    private readonly byte[] _xor;
    private readonly IEventSink _sink;
    private readonly IOutboundSource _outbound;
    private readonly IReadOnlyList<string> _checksums;
    private readonly Func<long> _clock;
    private readonly Action<string> _log;
    private FiestaSocket? _zone;
    private FiestaSocket? _wm;
    private CancellationTokenSource? _linkCts;
    private Endpoint? _handoffTo;
    private ushort _wmHandle;
    private string _charName = "";
    private readonly ChannelOutbound _wmOut = new();
    private Task? _wmLoop;
    private volatile bool _stopping;

    /// <summary>Frame tap (packet capture), both directions, both links.</summary>
    public FrameTap? Tap { get; set; }
    /// <summary>The zone endpoint currently connected.</summary>
    public Endpoint? Zone { get; private set; }
    /// <summary>Handoffs done.</summary>
    public int Handoffs { get; private set; }
    /// <summary>Zone frames received.</summary>
    public long FramesIn => _zone?.FramesIn ?? 0;
    /// <summary>Last error that ended the session (null = clean).</summary>
    public Exception? Error { get; private set; }

    /// <summary>Create for one bot.</summary>
    public ZoneSession(WireProfile wire, DecoderTable table, byte[] xorTable, IEventSink sink, IOutboundSource outbound,
        IReadOnlyList<string> checksums, Func<long> clock, Action<string> log)
    {
        _wire = wire; _table = table; _xor = xorTable; _sink = sink; _outbound = outbound; _checksums = checksums; _clock = clock; _log = log;
    }

    /// <summary>Enter the zone the login chain handed over and keep the session running until it closes (logout, link
    /// loss or cancellation). Cross-server handoffs reconnect inside this call.</summary>
    public async Task RunAsync(LoginResult login, CancellationToken ct)
    {
        _wmHandle = login.WmHandle;
        _charName = login.CharacterName;
        _wm = login.WorldManager;
        if (_wm is not null)
        {
            _wm.Tap = Tap;
            _wmLoop = Task.Run(() => _wm.RunLoopsAsync(new WmSink(this), _wmOut, ct), CancellationToken.None);
        }
        var ep = login.Zone;
        try
        {
            while (!ct.IsCancellationRequested && !_stopping)
            {
                _handoffTo = null;
                var zone = await FiestaSocket.ConnectAsync(ep, _xor, ct);
                zone.Tap = Tap; zone.Clock = _clock;
                _zone = zone; Zone = ep;
                _log($"[zone] connected {ep} seed=0x{zone.Seed:X4} ({_wire.Name} wire)");
                await zone.SendAsync(0x1801, _wire.BuildMapLogin(_wmHandle, _charName, _checksums), ct);
                _log($"[zone] >> MAP_LOGIN_REQ char='{_charName}' wm={_wmHandle} ({_checksums.Count} checksums)");
                Link(LinkState.Ready);
                _linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // the map login must be answered: a session the zone never lets into the world ends (the supervisor logs
                // in again) instead of sitting there forever
                _mapAcked = false; _mapLoginFailed = null;
                var linkCts = _linkCts;
                _ = Task.Delay(MapLoginTimeout, linkCts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled || _mapAcked) return;
                    _mapLoginFailed = $"no MAP_LOGIN_ACK within {MapLoginTimeout.TotalSeconds:0} s";
                    _log($"[zone] {_mapLoginFailed}: closing the session");
                    try { linkCts.Cancel(); } catch (ObjectDisposedException) { }
                }, TaskScheduler.Default);
                try { await zone.RunLoopsAsync(this, new WmRouter(_outbound, _wmOut), _linkCts.Token); }
                catch (OperationCanceledException) when (_mapLoginFailed is not null && !ct.IsCancellationRequested) { }
                if (_mapLoginFailed is { } why) { await zone.DisposeAsync(); throw new TimeoutException(why); }
                await zone.DisposeAsync();
                if (_handoffTo is { } next && !_stopping)
                {
                    Link(LinkState.Connecting);
                    _log($"[zone] handoff to {next}");
                    ep = next; Handoffs++;
                    continue;
                }
                break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e;
            _log($"[zone] session error: {e.Message}");
        }
        finally
        {
            Link(LinkState.Disconnected);
            if (_wm is not null) await _wm.DisposeAsync();
        }
    }

    private void Link(LinkState s)
    {
        var e = InboundEvent.Of(InboundKind.LinkChanged, 0, _clock()); e.A = (long)s;
        _sink.TryWrite(e);
    }

    /// <summary>How long the zone may take to answer MAP_LOGIN_REQ.</summary>
    public static TimeSpan MapLoginTimeout { get; set; } = TimeSpan.FromSeconds(20);
    private volatile bool _mapAcked;
    private volatile string? _mapLoginFailed;

    /// <inheritdoc/>
    public void OnFrame(in RawFrame f)
    {
        var p = f.Payload.Span;
        switch (f.Opcode)
        {
            case 0x0804:   // heartbeat: answered on the I/O thread
                _ = SendZone(IntentEncoder.OpHeartbeatAck, Array.Empty<byte>());
                return;
            case 0x1802:   // MAP_LOGIN_ACK -> the client's start-up frames, then MAP_LOGINCOMPLETE
            {
                _mapAcked = true;
                var zone = new List<(ushort, byte[])>(); var wm = new List<(ushort, byte[])>();
                _wire.AfterMapLoginAck(zone, wm);
                foreach (var (op, b) in wm) _wmOut.Post(op, b);
                foreach (var (op, b) in zone) _ = SendZone(op, b);
                _ = SendZone(IntentEncoder.OpLoginComplete, Array.Empty<byte>());
                var after = new List<(ushort, byte[])>();
                _wire.AfterLoginComplete(after);
                foreach (var (op, b) in after) _ = SendZone(op, b);
                _log("[zone] << MAP_LOGIN_ACK >> MAP_LOGINCOMPLETE: in the world");
                break;
            }
            case 0x1804:
                _mapLoginFailed = $"MAP_LOGINFAIL err={(p.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(p) : -1)} file index={(p.Length >= 3 ? p[2] : -1)}";
                _log($"[zone] << {_mapLoginFailed}");
                try { _linkCts?.Cancel(); } catch (ObjectDisposedException) { }
                break;
            case 0x1809:   // LINKSAME: the map changed on this zone; spawn into it
                _ = SendZone(IntentEncoder.OpLoginComplete, Array.Empty<byte>());
                break;
            case 0x180A:   // LINKOTHER: another zone server; reconnect there with MAP_LOGIN
                if (p.Length >= 30)
                {
                    var ip = Ascii.Get(p.Slice(10, 16));
                    var port = BinaryPrimitives.ReadUInt16LittleEndian(p[26..]);
                    _wmHandle = BinaryPrimitives.ReadUInt16LittleEndian(p[28..]);
                    var host = Zone?.Host ?? ip;   // the advertised address may not hairpin; the zone host does
                    _handoffTo = new Endpoint(host, port);
                    _table.Dispatch(f, _sink);
                    _linkCts?.Cancel();
                    return;
                }
                break;
        }
        _table.Dispatch(f, _sink);
    }

    private Task SendZone(ushort op, byte[] body)
    {
        var z = _zone;
        return z is null ? Task.CompletedTask : SafeSend(z, op, body);
    }

    private async Task SafeSend(FiestaSocket s, ushort op, byte[] body)
    {
        try { await s.SendAsync(op, body, CancellationToken.None); }
        catch (Exception e) { _log($"[zone] send 0x{op:X4} failed: {e.Message}"); }
    }

    /// <inheritdoc/>
    public void OnClosed(LinkState finalState, Exception? error)
    {
        if (error is not null && _handoffTo is null && !_stopping) { Error = error; _log($"[zone] link closed: {error.Message}"); }
    }

    /// <summary>Clean logout as the client does it: LOGOUTREADY on the zone, then NORMALLOGOUT on the world manager and
    /// the zone after the client's countdown. The caller must already have stopped moving and fighting.</summary>
    public async Task LogoutAsync(TimeSpan countdown, CancellationToken ct)
    {
        if (_zone is not { } z) return;
        try
        {
            await z.SendAsync(_wire.Opcode(OutMsg.LogoutReady), Array.Empty<byte>(), ct);
            await Task.Delay(countdown, ct);
            _stopping = true;
            var bye = new byte[] { 0 };
            if (_wm is not null) await _wm.SendAsync(_wire.Opcode(OutMsg.NormalLogout), bye, ct);
            await z.SendAsync(_wire.Opcode(OutMsg.NormalLogout), bye, ct);
            await Task.Delay(300, ct);
            _log("[zone] logged out (NORMALLOGOUT sent)");
        }
        catch (Exception e) { _log($"[zone] logout: {e.Message}"); }
        finally
        {
            _stopping = true;
            _linkCts?.Cancel();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _linkCts?.Cancel();
        if (_zone is not null) await _zone.DisposeAsync();
        if (_wm is not null) await _wm.DisposeAsync();
        if (_wmLoop is not null) try { await _wmLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    // answers that belong to the world manager (party / friend confirmations) leave through its link
    private sealed class WmRouter(IOutboundSource inner, ChannelOutbound wm) : IOutboundSource
    {
        public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => inner.WaitToReadAsync(ct);
        public bool TryDequeue(out ushort opcode, out ReadOnlyMemory<byte> payload)
        {
            while (inner.TryDequeue(out opcode, out payload))
            {
                if (opcode is not (IntentEncoder.OpPartyAllow or IntentEncoder.OpFriendConfirm)) return true;
                wm.Post(opcode, payload.ToArray());
            }
            return false;
        }
    }

    private sealed class WmSink(ZoneSession s) : IFrameSink
    {
        public void OnFrame(in RawFrame frame)
        {
            var p = frame.Payload.Span;
            switch (frame.Opcode)
            {
                case 0x0804: s._wmOut.Post(IntentEncoder.OpHeartbeatAck, Array.Empty<byte>()); break;
                case 0x3803 when p.Length >= 20:   // PARTY_JOINPROPOSE_REQ {mastername[20]}
                {
                    var e = InboundEvent.Of(InboundKind.PartyProposal, frame.Opcode, s._clock()); e.Text = Ascii.Get(p[..20]);
                    s._sink.TryWrite(e);
                    break;
                }
                case 0x5403 when p.Length >= 40:   // FRIEND_SET_CONFIRM_REQ {charid[20], friendid[20]}
                {
                    var e = InboundEvent.Of(InboundKind.FriendProposal, frame.Opcode, s._clock()); e.Text = Ascii.Get(p[..20]) + "\t" + Ascii.Get(p.Slice(20, 20));
                    s._sink.TryWrite(e);
                    break;
                }
            }
        }
        public void OnClosed(LinkState finalState, Exception? error)
        {
            if (!s._stopping) s._log($"[wm] link closed{(error is null ? "" : ": " + error.Message)}");
        }
    }
}
