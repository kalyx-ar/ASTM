using Astm.Abstractions.Transport;
using Astm.Transport;

namespace PortGate.Client;

/// <summary>What an agent announces on accept (spec/link-v1.md, Hello and Capabilities).</summary>
public sealed record PortGateAgentDescription(string AgentId, Guid AgentEpoch, string? Platform, bool ObserverSoftKill,
    IReadOnlyList<PortGateAnnouncedPort> Ports);

/// <summary>One port of <see cref="PortGateAgentDescription"/>. <see cref="State"/> is present only when it was observed.</summary>
public sealed record PortGateAnnouncedPort(string PortId, string? InstrumentId, string Ownership, string TraceCapture, int MaxWriteBytes,
    bool QueuedAcquisition, PortGateAnnouncedDevice? Device)
{
    public string? VirtualCom { get; init; }
    public PortGatePortState? State { get; init; }
    public string? StateCode { get; init; }
}

/// <summary>Device in force as the agent announces it: path for serial devices, host/port for TCP ones.</summary>
public sealed record PortGateAnnouncedDevice(string Kind, string? Path, string? Host, int? Port, SerialSettings? Serial);

/// <summary>
/// Read-only inspection of an agent: it never acquires a port nor changes the agent configuration. With observation,
/// it asks for the semaphore of every port (right 4 on the port) and stops observing at once; the agent records both
/// events (spec/admin-v1.md). Traffic copies of a shared port are discarded unread.
/// </summary>
public static class PortGateDiscovery
{
    public static async Task<PortGateAgentDescription> DescribeAsync(TcpEndpoint agent, string controllerId, bool observe,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(controllerId)) throw new ArgumentException("controller id required", nameof(controllerId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stream = await LocalTransportProvider.ConnectAsync(agent, deadline.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
            return await DescribeAsync(stream, controllerId, observe, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Inspects an established connection (the caller owns <paramref name="stream"/>).</summary>
    public static async Task<PortGateAgentDescription> DescribeAsync(Stream stream, string controllerId, bool observe, CancellationToken cancellationToken)
    {
        var decoder = new LinkDecoder(64 * 1024);
        var units = new List<LinkUnit>();
        var hello = await PortGateChannel.ReceiveAsync(stream, decoder, units, LinkType.Hello, cancellationToken).ConfigureAwait(false);
        var capabilities = await PortGateChannel.ReceiveAsync(stream, decoder, units, LinkType.Capabilities, cancellationToken).ConfigureAwait(false);
        await PortGateChannel.WriteAsync(stream, LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "controller"), (2, controllerId))),
            cancellationToken).ConfigureAwait(false);
        var helloMap = hello.Map() ?? throw new PortGateException("PORTGATE-CH-BAD-UNIT", "Hello without CBOR map");
        var capabilityMap = capabilities.Map() ?? throw new PortGateException("PORTGATE-CH-BAD-UNIT", "Capabilities without CBOR map");
        var ports = (capabilityMap.GetValueOrDefault(0UL) as List<object?> ?? []).Select(Port).ToList();
        var description = new PortGateAgentDescription(helloMap.GetValueOrDefault(2UL) as string ?? "",
            helloMap.GetValueOrDefault(3UL) is byte[] { Length: 16 } epoch ? new Guid(epoch) : Guid.Empty,
            helloMap.GetValueOrDefault(4UL) as string, capabilityMap.GetValueOrDefault(1UL) is true, ports);
        if (!observe || ports.Count == 0)
            return description;

        // Request ids are 1-based port indexes, so every reply maps to its port.
        for (var i = 0; i < ports.Count; i++)
            await PortGateChannel.WriteAsync(stream, LinkUnit.Control(LinkType.Observe, (ushort)(i + 1), LinkCbor.Map((0, (ulong)(i + 1)), (1, true))),
                cancellationToken).ConfigureAwait(false);
        var states = new PortGatePortState?[ports.Count];
        var codes = new string?[ports.Count];
        var observing = new bool[ports.Count];
        var pending = ports.Count;
        var buffer = new byte[16 * 1024];
        while (pending > 0)
        {
            foreach (var unit in units)
            {
                var map = unit.Map();
                var index = unit.Channel - 1;
                switch (unit.Type)
                {
                    case LinkType.ObserveResult when map is not null && map.GetValueOrDefault(0UL) is ulong id && id >= 1 && id <= (ulong)ports.Count
                        && codes[id - 1] is null && !observing[id - 1]:
                        if (map.GetValueOrDefault(1UL) is true) observing[id - 1] = true;
                        else { codes[id - 1] = map.GetValueOrDefault(2UL) as string ?? "PORTGATE-OBSERVE-REFUSED"; pending--; }
                        break;
                    case LinkType.PortState when map is not null && index >= 0 && index < ports.Count && observing[index] && states[index] is null:
                        states[index] = new PortGatePortState(map.GetValueOrDefault(0UL) is true, map.GetValueOrDefault(1UL) as string,
                            (uint)(map.GetValueOrDefault(2UL) as ulong? ?? 0), (int)(map.GetValueOrDefault(3UL) as ulong? ?? 0), map.GetValueOrDefault(4UL) is true,
                            map.GetValueOrDefault(5UL) is true, map.GetValueOrDefault(6UL) is true)
                        { NextObservationSequence = map.GetValueOrDefault(8UL) as ulong? ?? 0 };
                        pending--;
                        break;
                    case LinkType.Error when map is not null && map.GetValueOrDefault(1UL) is ulong failed && failed >= 1 && failed <= (ulong)ports.Count
                        && codes[failed - 1] is null && states[failed - 1] is null:
                        codes[failed - 1] = map.GetValueOrDefault(0UL) as string ?? "PORTGATE-CH-AGENT-ERROR";
                        pending--;
                        break;
                    case LinkType.Error:
                        throw new PortGateException(map?.GetValueOrDefault(0UL) as string ?? "PORTGATE-CH-AGENT-ERROR", "agent refused the inspection");
                    // ObservedData of shared ports, SecurityEvent and Heartbeat are discarded unread.
                }
            }
            units.Clear();
            if (pending == 0) break;
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("Agent closed the connection during the inspection.");
            if (decoder.Feed(buffer.AsSpan(0, count), units) != LinkDecodeError.None)
                throw new PortGateException("PORTGATE-CH-BAD-UNIT", decoder.Error.ToString());
        }
        for (var i = 0; i < ports.Count; i++)
        {
            if (observing[i])
                await PortGateChannel.WriteAsync(stream, LinkUnit.Control(LinkType.Observe, (ushort)(i + 1), LinkCbor.Map((0, (ulong)(ports.Count + i + 1)), (1, false))),
                    cancellationToken).ConfigureAwait(false);
            ports[i] = ports[i] with { State = states[i], StateCode = codes[i] };
        }
        return description with { Ports = ports };
    }

    private static PortGateAnnouncedPort Port(object? value)
    {
        if (value is not SortedDictionary<ulong, object?> p || p.GetValueOrDefault(0UL) is not string id)
            throw new PortGateException("PORTGATE-CH-BAD-UNIT", "Capabilities port without id");
        var device = p.GetValueOrDefault(7UL) as SortedDictionary<ulong, object?>;
        var kind = (device?.GetValueOrDefault(0UL) as ulong?) switch { 0 => "serial", 1 => "tcp", 2 => "tcp-listen", null => null, _ => "unknown" };
        return new(id, p.GetValueOrDefault(1UL) as string,
            (p.GetValueOrDefault(2UL) as ulong?) switch { 0 => "none", 1 => "advisory", 2 => "enforced", _ => "unknown" },
            (p.GetValueOrDefault(3UL) as ulong?) switch { 1 => "ram", 2 => "durable", _ => "none" },
            (int)Math.Min(p.GetValueOrDefault(5UL) as ulong? ?? 16 * 1024, int.MaxValue), p.GetValueOrDefault(6UL) is true,
            kind is null ? null : new(kind, device!.GetValueOrDefault(1UL) as string is { Length: > 0 } path ? path : null,
                device!.GetValueOrDefault(7UL) as string is { Length: > 0 } host ? host : null,
                device!.GetValueOrDefault(8UL) is ulong port and > 0 and <= 65535 ? (int)port : null, PortGateDevices.Serial(device)))
        { VirtualCom = p.GetValueOrDefault(8UL) as string is { Length: > 0 } vcom ? vcom : null };
    }
}
