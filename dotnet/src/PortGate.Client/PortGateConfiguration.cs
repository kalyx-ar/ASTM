using Atsm.Abstractions.Transport;

namespace PortGate.Client;

/// <summary>Rights of a controller on a port besides using it (spec/admin-v1.md).</summary>
[Flags]
public enum PortGateRights : ulong { None = 0, KillSwitch = 1, Contest = 2 }

public sealed record PortGateController(string Id, int Priority = 0, string? CertificateSha256 = null, PortGateRights Rights = PortGateRights.None);

/// <summary>
/// Device of a port: a local serial port of the agent with the same <see cref="SerialSettings"/> a local COM would use,
/// an Ethernet instrument (or terminal server) the agent connects to, or one that connects to the agent.
/// </summary>
public sealed record PortGateDevice
{
    public string? Path { get; init; }
    public SerialSettings Settings { get; init; } = new();
    public string? Host { get; init; }
    public int TcpPort { get; init; }
    /// <summary>The instrument connects to the agent (it is a TCP client) on <see cref="Host"/>:<see cref="TcpPort"/>.</summary>
    public bool Listen { get; init; }

    public static PortGateDevice Serial(string path, SerialSettings? settings = null) => new() { Path = path, Settings = settings ?? new() };

    public static PortGateDevice Tcp(string host, int port) => new() { Host = host, TcpPort = port };

    public static PortGateDevice TcpListen(int port, string host = "0.0.0.0") => new() { Host = host, TcpPort = port, Listen = true };

    internal SortedDictionary<ulong, object?> ToMap()
    {
        if (Path is not null)
        {
            var map = LinkCbor.Map((0, 0UL), (1, Path));
            PortGateDevices.Apply(map, Settings);
            return map;
        }
        return LinkCbor.Map((0, Listen ? 2UL : 1UL), (7, Host ?? throw new InvalidOperationException("Device needs a serial path or a TCP host.")), (8, (ulong)TcpPort));
    }
}

public sealed record PortGatePort(string PortId, string InstrumentId, PortGateDevice Device, IReadOnlyList<PortGateController> Controllers)
{
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxQueuedRequests { get; init; } = 16;
    /// <summary>Maximum continuous tenure while others wait; <see cref="TimeSpan.Zero"/> disables it.</summary>
    public TimeSpan MaxHold { get; init; }
    public int MaxWriteBytes { get; init; } = 16 * 1024;

    internal SortedDictionary<ulong, object?> ToMap() => LinkCbor.Map(
        (0, PortId), (1, InstrumentId), (2, Device.ToMap()), (3, (ulong)Lease.TotalMilliseconds), (4, (ulong)MaxQueuedRequests),
        (5, (ulong)MaxHold.TotalMilliseconds),
        (6, Controllers.Select(c =>
        {
            var map = LinkCbor.Map((0, c.Id), (1, (ulong)c.Priority));
            if (c.CertificateSha256 is not null)
                map[2] = c.CertificateSha256;
            if (c.Rights != PortGateRights.None)
                map[3] = (ulong)c.Rights;
            return (object?)map;
        }).ToList()),
        (7, (ulong)MaxWriteBytes));
}

public sealed record PortGateSecurity
{
    public bool KillSwitch { get; init; }
    public bool Contest { get; init; }
    public TimeSpan Quarantine { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan KillCooldown { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan ContestBlock { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan HandoverInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HandoverTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal SortedDictionary<ulong, object?> ToMap() => LinkCbor.Map(
        (0, KillSwitch), (1, Contest), (2, (ulong)Quarantine.TotalMilliseconds), (3, (ulong)KillCooldown.TotalMilliseconds),
        (4, (ulong)ContestBlock.TotalMilliseconds), (5, (ulong)HandoverInterval.TotalMilliseconds), (6, (ulong)HandoverTimeout.TotalMilliseconds));
}

/// <summary>Complete configuration the administering server installs on a PortGate agent (spec/admin-v1.md § Configuration map).</summary>
public sealed record PortGateConfiguration(string AgentId, IReadOnlyList<PortGatePort> Ports)
{
    public string PlatformProfile { get; init; } = "unspecified";
    public bool Restricted { get; init; } = true;
    public bool AdminRequiresTls { get; init; }
    public string ListenHost { get; init; } = "0.0.0.0";
    public int ListenPort { get; init; }
    public bool Tls { get; init; }
    public bool RequireClientCertificate { get; init; } = true;
    public bool MinimumTls13 { get; init; }
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(5);
    public PortGateSecurity Security { get; init; } = new();

    public SortedDictionary<ulong, object?> ToMap() => LinkCbor.Map(
        (0, AgentId), (1, PlatformProfile),
        (2, LinkCbor.Map((0, 0UL), (3, Restricted), (4, AdminRequiresTls))),
        (3, LinkCbor.Map((0, ListenHost), (1, (ulong)ListenPort), (2, Tls), (3, RequireClientCertificate), (4, MinimumTls13))),
        (4, (ulong)Heartbeat.TotalMilliseconds),
        (6, Security.ToMap()),
        (7, Ports.Select(p => (object?)p.ToMap()).ToList()));
}
