using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Atsm.Abstractions.Transport;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugin.Diagast.Qwalys;
using Atsm.Plugin.Sysmex.Cs2500;
using Atsm.Transport;

namespace Atsm.Tests.Transport;

public class TransportTests
{
    [Fact]
    public void Serial_settings_map_to_the_platform_port_without_opening_it()
    {
        using var port = LocalSerial.Configure(new SerialPort("COM250"),
            new SerialSettings(19200, 7, SerialParity.Even, SerialStopBits.Two, SerialFlowControl.XonXoff) { Dtr = false, Rts = false });
        Assert.Equal((19200, 7, Parity.Even, StopBits.Two, Handshake.XOnXOff, false, false),
            (port.BaudRate, port.DataBits, port.Parity, port.StopBits, port.Handshake, port.DtrEnable, port.RtsEnable));
        Assert.False(port.IsOpen);

        // With RTS/CTS the driver owns RTS: the setting is not applied (it would throw).
        using var hardware = LocalSerial.Configure(new SerialPort("COM250"), new SerialSettings(FlowControl: SerialFlowControl.RtsCts) { Rts = false });
        Assert.Equal(Handshake.RequestToSend, hardware.Handshake);
    }

    [Fact]
    public void Plugins_declare_the_transport_of_their_manuals_and_settings_are_checked_against_it()
    {
        var architect = new ArchitectPlugin().Profiles[0].Transport;
        Assert.Equal(new SerialSettings(9600), architect.DefaultSerialSettings());
        Assert.NotEmpty(architect!.Evidence);
        Assert.Empty(architect.Validate(new SerialSettings(9600)));
        Assert.Single(architect.Validate(new SerialSettings(19200)));

        var qwalys = new QwalysPlugin().Profiles[0].Transport;
        Assert.Empty(qwalys.Validate(new SerialSettings(38400)));

        var cs2500 = new Cs2500Plugin().Profiles[0].Transport!;
        Assert.Equal((NetworkRole.InstrumentIsClient, 5000), (cs2500.Network!.Role, cs2500.Network.DefaultPort));
    }

    [Fact]
    public async Task Tcp_client_and_listen_targets_return_connected_streams()
    {
        var provider = new LocalTransportProvider();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var accepting = server.AcceptTcpClientAsync();
        await using (var client = await provider.OpenAsync(new TcpClientTarget("i1", new TcpEndpoint("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port)), CancellationToken.None))
        {
            using var peer = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            await client.WriteAsync(new byte[] { 0x05 });
            var received = new byte[1];
            await peer.GetStream().ReadExactlyAsync(received);
            Assert.Equal(0x05, received[0]);
        }

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var listening = provider.OpenAsync(new TcpListenTarget("i2", new IPEndPoint(IPAddress.Loopback, port)), CancellationToken.None).AsTask();
        using var instrument = new TcpClient();
        for (var attempt = 0; !instrument.Connected && attempt < 50; attempt++)
        {
            try { await instrument.ConnectAsync(IPAddress.Loopback, port); }
            catch (SocketException) { await Task.Delay(20); }
        }
        await using var stream = await listening.WaitAsync(TimeSpan.FromSeconds(5));
        await instrument.GetStream().WriteAsync(new byte[] { 0x06 });
        var answer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(answer));
        Assert.Equal(0x06, answer[0]);
    }

    [Fact]
    public async Task Router_chooses_the_provider_and_reports_unsupported_targets()
    {
        var router = new TransportRouter([new LocalTransportProvider()]);
        Assert.True(router.Supports(new SerialTarget("s", "COM250", new SerialSettings())));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await router.OpenAsync(new Unknown("x"), CancellationToken.None));
        // A missing local port fails explicitly instead of returning a dead stream.
        await Assert.ThrowsAnyAsync<Exception>(async () => await router.OpenAsync(new SerialTarget("s", "COM250", new SerialSettings()), CancellationToken.None));
    }

    private sealed record Unknown(string Id) : TransportTarget(Id);
}
