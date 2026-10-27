using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Astm.Transport;

namespace PortGate.Client;

/// <summary>
/// Server side of the agent-initiated mode (spec/admin-v1.md): agents dial this listener, so they need no inbound port.
/// The server speaks first and gives each accepted connection its role by opening it with
/// <see cref="PortGateAdminClient.OpenAsync"/> or <see cref="PortGateChannel.OpenAsync"/>.
/// </summary>
public sealed class PortGateListener(IPEndPoint local, TlsOptions? tls = null) : IDisposable
{
    private readonly TcpListener _listener = new(local);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start() => _listener.Start();

    public Task<Stream> AcceptAsync(CancellationToken cancellationToken = default) => LocalTransportProvider.AcceptAsync(_listener, tls, cancellationToken);

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Client-certificate validation that accepts only agents whose self-signed certificate is pinned by its SHA-256
    /// (reported by GetStatus and printed by the agent at start).
    /// </summary>
    public static RemoteCertificateValidationCallback PinAgents(params string[] sha256)
    {
        var pinned = sha256.Select(s => s.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return (_, certificate, _, _) => certificate is not null && pinned.Contains(Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())));
    }
}
