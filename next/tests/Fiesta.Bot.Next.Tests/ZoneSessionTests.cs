using System.Net;
using System.Net.Sockets;
using Fiesta.Bot.Net;
using Fiesta.Bot.Protocol;
using Xunit;

namespace Fiesta.Bot.Next.Tests;

public class ZoneSessionTests
{
    private sealed class NullSink : IEventSink { public bool TryWrite(in InboundEvent evt) => true; }

    [Fact]
    public async Task A_zone_that_never_answers_the_map_login_ends_the_session()
    {
        // a fake zone: sends the seed handshake, then reads and never answers
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var st = c.GetStream();
            await st.WriteAsync(new byte[] { 4, 0x07, 0x08, 0x12, 0x00 });   // len 4: opcode 0x0807, seed 0x0012
            var buf = new byte[4096];
            try { while (await st.ReadAsync(buf) > 0) { } } catch (IOException) { }
        });
        var old = ZoneSession.MapLoginTimeout;
        ZoneSession.MapLoginTimeout = TimeSpan.FromSeconds(1);
        try
        {
            var wire = WireProfile.For(WireKind.Wire2026);
            var session = new ZoneSession(wire, DecoderTable.CreateDefault(wire), new byte[499], new NullSink(), new ChannelOutbound(),
                Enumerable.Repeat(new string('0', 32), 53).ToArray(), () => 0, _ => { });
            var login = new LoginResult(true, null, new Fiesta.Bot.Net.Endpoint("127.0.0.1", port), 1, "Tester", 1);
            var run = session.RunAsync(login, CancellationToken.None);
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(10000)));
            Assert.IsType<TimeoutException>(session.Error);
        }
        finally { ZoneSession.MapLoginTimeout = old; listener.Stop(); }
    }
}
