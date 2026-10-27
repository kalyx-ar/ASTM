using System.Net;
using Atsm.Abstractions.Transport;
using Atsm.Transport;
using PortGate.Client;

namespace Atsm.Diagnostics;

public enum DiagnosticTransport { TcpClient, TcpServer, PortGate, LocalSerial }

/// <summary>The maintenance application owns transport selection; protocols receive only the resulting Stream.</summary>
public sealed record DiagnosticConnection(DiagnosticTransport Kind, string Host, int Port)
{
    public string SerialPort { get; init; } = "";
    public SerialSettings Serial { get; init; } = new();
    public bool ConfigureSerial { get; init; }
    public string AgentPort { get; init; } = "";
    public string Controller { get; init; } = "";
    public TlsOptions? Tls { get; init; }
    public TransportTarget Target()
    {
        if (Kind == DiagnosticTransport.LocalSerial)
        {
            if (string.IsNullOrWhiteSpace(SerialPort)) throw new ArgumentException("ATSM-DIAG-SERIAL-PORT");
            return new SerialTarget("maintenance", SerialPort.Trim(), Serial);
        }
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535) throw new ArgumentException("ATSM-DIAG-ENDPOINT");
        return Kind switch
        {
            DiagnosticTransport.PortGate when !string.IsNullOrWhiteSpace(AgentPort) && !string.IsNullOrWhiteSpace(Controller) =>
                new PortGateTarget("maintenance", new TcpEndpoint(Host, Port) { Tls = Tls }, AgentPort, Controller) { Settings = ConfigureSerial ? Serial : null },
            DiagnosticTransport.TcpServer => new TcpListenTarget("maintenance", new IPEndPoint(IPAddress.Parse(Host), Port)) { Tls = Tls },
            DiagnosticTransport.TcpClient => new TcpClientTarget("maintenance", new TcpEndpoint(Host, Port) { Tls = Tls }),
            _ => throw new ArgumentException("ATSM-DIAG-TRANSPORT"),
        };
    }
}
