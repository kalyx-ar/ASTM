using System.Net.Sockets;
using System.Security.Authentication;
using Astm.Transport;
using PortGate.Client;

namespace Astm.Service;

/// <summary>Read-only inspection of a PortGate agent (Hello/Capabilities, optional semaphore). Never acquires nor configures.</summary>
public interface IPortGateProbe
{
    Task<PortGateAgentDescription> DescribeAsync(GatewayConfiguration gateway, CancellationToken token);
}

public sealed class PortGateProbe : IPortGateProbe
{
    public async Task<PortGateAgentDescription> DescribeAsync(GatewayConfiguration gateway, CancellationToken token)
    {
        var tls = ServiceTransportFactory.Tls(new TransportConfiguration("portgate")
        {
            Host = gateway.Host, Port = gateway.Port, Tls = gateway.Tls, CertificateSha256 = gateway.CertificateSha256,
            TlsTargetHost = gateway.TlsTargetHost, ClientCertificatePath = gateway.ClientCertificatePath,
        });
        try
        {
            return await PortGateDiscovery.DescribeAsync(new TcpEndpoint(gateway.Host, gateway.Port)
            { Tls = tls, ConnectTimeout = TimeSpan.FromMilliseconds(gateway.ConnectTimeoutMs) },
                gateway.ControllerId, gateway.Observe, TimeSpan.FromMilliseconds(gateway.ConnectTimeoutMs), token).ConfigureAwait(false);
        }
        finally
        {
            if (tls?.ClientCertificates is not null) foreach (var certificate in tls.ClientCertificates) certificate.Dispose();
        }
    }

    /// <summary>Technical code of a failed inspection; never an exception message.</summary>
    internal static string Code(Exception exception, CancellationToken request) => exception switch
    {
        PortGateException known => known.Code,
        OperationCanceledException when !request.IsCancellationRequested => "gateway.timeout",
        AuthenticationException => "gateway.tls",
        ServiceException known => known.Code,
        SocketException or IOException => "gateway.unreachable",
        _ => "gateway.failed",
    };
}
