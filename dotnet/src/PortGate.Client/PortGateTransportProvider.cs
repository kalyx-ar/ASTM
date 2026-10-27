using Astm.Abstractions.Transport;
using Astm.Transport;

namespace PortGate.Client;

/// <summary>A port behind a PortGate agent. <see cref="Settings"/> are the same serial settings a local COM would use.</summary>
public sealed record PortGateTarget(string Id, TcpEndpoint Agent, string PortId, string ControllerId) : TransportTarget(Id)
{
    /// <summary>Required serial settings; null accepts whatever the agent has in force.</summary>
    public SerialSettings? Settings { get; init; }

    public int Priority { get; init; }

    public TimeSpan? Deadline { get; init; }

    public PortGateOptions? Options { get; init; }
}

/// <summary>Device map of the agent configuration (spec/admin-v1.md) ↔ <see cref="SerialSettings"/>.</summary>
public static class PortGateDevices
{
    public static SerialSettings? Serial(SortedDictionary<ulong, object?>? device)
    {
        if (device is null || (device.GetValueOrDefault(0UL) as ulong?) != 0)
            return null;
        return new SerialSettings(
            (int)(device.GetValueOrDefault(2UL) as ulong? ?? 9600),
            (int)(device.GetValueOrDefault(3UL) as ulong? ?? 8),
            (SerialParity)(device.GetValueOrDefault(4UL) as ulong? ?? 0),
            (device.GetValueOrDefault(5UL) as ulong?) == 2 ? SerialStopBits.Two : SerialStopBits.One,
            (SerialFlowControl)(device.GetValueOrDefault(6UL) as ulong? ?? 0))
        {
            Dtr = device.GetValueOrDefault(9UL) is not false,
            Rts = device.GetValueOrDefault(10UL) is not false,
        };
    }

    /// <summary>Writes <paramref name="settings"/> into a serial device map, keeping its path.</summary>
    public static void Apply(SortedDictionary<ulong, object?> device, SerialSettings settings)
    {
        if (settings.StopBits == SerialStopBits.OnePointFive)
            throw new PortGateException("PORTGATE-CH-SETTINGS-UNSUPPORTED", "PortGate supports 1 or 2 stop bits");
        device[2] = (ulong)settings.BaudRate;
        device[3] = (ulong)settings.DataBits;
        device[4] = (ulong)settings.Parity;
        device[5] = settings.StopBits == SerialStopBits.Two ? 2UL : 1UL;
        device[6] = (ulong)settings.FlowControl;
        device[9] = settings.Dtr;
        device[10] = settings.Rts;
    }
}

/// <summary>
/// Opens a port behind PortGate as the final transport of an ASTM session. Required serial settings are compared with
/// the ones the agent announces; when they differ and an administrator is available, they are applied behind the tunnel
/// with <c>SetConfig</c> (the agent restarts its Link endpoint). Otherwise opening fails rather than using other settings.
/// </summary>
public sealed class PortGateTransportProvider(Func<PortGateTarget, CancellationToken, Task<PortGateAdminClient>>? administrator = null) : ITransportProvider
{
    public bool Supports(TransportTarget target) => target is PortGateTarget;

    public async ValueTask<Stream> OpenAsync(TransportTarget target, CancellationToken cancellationToken)
    {
        var port = (PortGateTarget)target;
        var options = port.Options ?? new PortGateOptions(port.ControllerId, port.PortId);
        var agent = port.Agent;
        var channel = await PortGateChannel.ConnectAsync(agent, options, cancellationToken).ConfigureAwait(false);
        try
        {
            if (port.Settings is { } required && channel.Port.Serial != required)
            {
                if (channel.Port.DeviceKind != "serial")
                    throw new PortGateException("PORTGATE-CH-NOT-SERIAL", $"port {port.PortId} is a {channel.Port.DeviceKind ?? "unknown"} device");
                if (administrator is null)
                    throw new PortGateException("PORTGATE-CH-SETTINGS-MISMATCH", $"port {port.PortId} has {channel.Port.Serial} and {required} is required");
                await channel.DisposeAsync().ConfigureAwait(false);
                var linkPort = await ApplyAsync(port, required, cancellationToken).ConfigureAwait(false);
                agent = agent with { Port = linkPort };
                channel = await PortGateChannel.ConnectAsync(agent, options, cancellationToken).ConfigureAwait(false);
                if (channel.Port.Serial != required)
                    throw new PortGateException("PORTGATE-CH-SETTINGS-MISMATCH", "the agent did not apply the required settings");
            }
            await channel.AcquireAsync(port.Priority, port.Deadline, cancellationToken).ConfigureAwait(false);
            return new OwnedStream(channel.Stream!, new AsyncOwner(channel));
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> ApplyAsync(PortGateTarget port, SerialSettings settings, CancellationToken cancellationToken)
    {
        await using var admin = await administrator!(port, cancellationToken).ConfigureAwait(false);
        var (revision, configuration) = await admin.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        var ports = configuration?.GetValueOrDefault(7UL) as List<object?> ?? throw new PortGateException("PORTGATE-CH-NOT-CONFIGURED", "the agent has no configuration");
        var entry = ports.OfType<SortedDictionary<ulong, object?>>().FirstOrDefault(p => p.GetValueOrDefault(0UL) as string == port.PortId)
            ?? throw new PortGateException("PORTGATE-CH-UNKNOWN-PORT", $"port {port.PortId} is not configured");
        PortGateDevices.Apply((SortedDictionary<ulong, object?>)entry[2]!, settings);
        var body = await admin.RequestAsync(LinkType.SetConfig, LinkCbor.Map((1, revision + 1), (2, configuration)), cancellationToken).ConfigureAwait(false);
        return (int)(body.GetValueOrDefault(1UL) as ulong? ?? (ulong)port.Agent.Port);
    }

    private sealed class AsyncOwner(PortGateChannel channel) : IDisposable
    {
        public void Dispose() => channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
