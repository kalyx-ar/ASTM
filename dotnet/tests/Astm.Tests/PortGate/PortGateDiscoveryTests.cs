using Astm.Abstractions.Transport;
using Astm.Testing;
using PortGate.Client;

namespace Astm.Tests.PortGate;

/// <summary>Link-level inspection against a scripted agent (spec/link-v1.md); runs without a PortGate build.</summary>
public sealed class PortGateDiscoveryTests
{
    [Fact]
    public async Task Describe_reads_capabilities_and_observes_only_where_allowed_without_acquiring()
    {
        var (controller, agent) = DuplexStream.CreatePair();
        await using var _ = agent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var epoch = Guid.NewGuid();
        var serial = LinkCbor.Map((0, 0UL), (1, "COM3"), (2, 9600UL), (3, 8UL), (4, 2UL), (5, 1UL), (6, 0UL), (9, true), (10, false));
        var tcp = LinkCbor.Map((0, 1UL), (7, "192.0.2.10"), (8, 5000UL));
        await agent.WriteAsync(LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "agent"), (2, "agent-1"), (3, epoch.ToByteArray()), (4, "win-x64"))).Encode(), timeout.Token);
        await agent.WriteAsync(LinkUnit.Control(LinkType.Capabilities, 0, LinkCbor.Map((0, new List<object?>
        {
            LinkCbor.Map((0, "com3"), (1, "analyzer-a"), (2, 2UL), (3, 2UL), (4, false), (5, 4096UL), (6, true), (7, serial), (8, "COM15")),
            LinkCbor.Map((0, "lan1"), (1, "analyzer-b"), (2, 2UL), (3, 0UL), (4, false), (5, 16384UL), (6, false), (7, tcp)),
        }), (1, true))).Encode(), timeout.Token);

        var describing = PortGateDiscovery.DescribeAsync(controller, "astm-service/discovery", observe: true, timeout.Token);
        var received = new List<LinkUnit>();
        var decoder = new LinkDecoder();
        var buffer = new byte[4096];
        async Task<LinkUnit> Next()
        {
            while (received.Count == 0)
            {
                var count = await agent.ReadAsync(buffer, timeout.Token);
                Assert.True(count > 0);
                decoder.Feed(buffer.AsSpan(0, count), received);
            }
            var unit = received[0]; received.RemoveAt(0); return unit;
        }
        var hello = await Next();
        Assert.Equal((LinkType.Hello, "astm-service/discovery"), (hello.Type, hello.Map()![2UL] as string));
        var first = await Next(); var second = await Next();
        Assert.Equal((LinkType.Observe, LinkType.Observe), (first.Type, second.Type));
        await agent.WriteAsync(LinkUnit.Control(LinkType.ObserveResult, 2, LinkCbor.Map((0, 2UL), (1, false), (2, "PORTGATE-OBSERVE-NOT-ALLOWED"))).Encode(), timeout.Token);
        await agent.WriteAsync(LinkUnit.Control(LinkType.ObserveResult, 1, LinkCbor.Map((0, 1UL), (1, true), (3, true))).Encode(), timeout.Token);
        // A shared port may send traffic copies before its state: they are discarded unread.
        await agent.WriteAsync(LinkUnit.Serial(1, 1, 0, 0, [0x05]).Encode(), timeout.Token);
        await agent.WriteAsync(LinkUnit.Control(LinkType.PortState, 1, LinkCbor.Map((0, true), (1, "astm-service/a"), (2, 7UL), (3, 1UL), (6, true))).Encode(), timeout.Token);

        var description = await describing.WaitAsync(timeout.Token);
        var stop = await Next();
        Assert.Equal((LinkType.Observe, (ushort)1, false), (stop.Type, stop.Channel, stop.Map()![1UL] is true));
        Assert.Empty(received); // Never Acquire, Release or anything else.

        Assert.Equal(("agent-1", epoch, "win-x64", true), (description.AgentId, description.AgentEpoch, description.Platform, description.ObserverSoftKill));
        var com3 = description.Ports[0];
        Assert.Equal(("com3", "analyzer-a", "enforced", "durable", 4096, true, "COM15"),
            (com3.PortId, com3.InstrumentId, com3.Ownership, com3.TraceCapture, com3.MaxWriteBytes, com3.QueuedAcquisition, com3.VirtualCom));
        Assert.Equal(("serial", "COM3"), (com3.Device!.Kind, com3.Device.Path));
        Assert.Equal(new SerialSettings(9600, 8, SerialParity.Even) { Dtr = true, Rts = false }, com3.Device.Serial);
        Assert.Equal(new PortGatePortState(true, "astm-service/a", 7, 1, false, false, true), com3.State);
        var lan = description.Ports[1];
        Assert.Equal(("tcp", "192.0.2.10", 5000), (lan.Device!.Kind, lan.Device.Host, lan.Device.Port));
        Assert.Null(lan.Device.Serial); Assert.Null(lan.State); Assert.Equal("PORTGATE-OBSERVE-NOT-ALLOWED", lan.StateCode);
    }

    [Fact]
    public async Task Describe_without_observation_only_sends_hello_and_reports_refusals()
    {
        var (controller, agent) = DuplexStream.CreatePair();
        await using var _ = agent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await agent.WriteAsync(LinkUnit.Control(LinkType.Error, 0, LinkCbor.Map((0, "PORTGATE-IDENTITY-MISMATCH"), (1, 0UL))).Encode(), timeout.Token);
        var error = await Assert.ThrowsAsync<PortGateException>(() => PortGateDiscovery.DescribeAsync(controller, "x", observe: false, timeout.Token));
        Assert.Equal("PORTGATE-IDENTITY-MISMATCH", error.Code);
    }
}
