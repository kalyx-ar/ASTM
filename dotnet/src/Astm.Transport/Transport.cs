using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Astm.Abstractions.Transport;

namespace Astm.Transport;

/// <summary>TLS of one endpoint. Never downgraded; validation uses the platform trust store unless a callback pins it.</summary>
public sealed record TlsOptions
{
    public string? TargetHost { get; init; }
    public X509CertificateCollection? ClientCertificates { get; init; }
    /// <summary>Server side only (an instrument that connects to the host).</summary>
    public X509Certificate? ServerCertificate { get; init; }
    public bool RequireClientCertificate { get; init; }
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }
    public SslProtocols Protocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;
}

public sealed record TcpEndpoint(string Host, int Port)
{
    public TlsOptions? Tls { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Where the instrument is. The same <see cref="SerialSettings"/> configure a local COM or a port behind a tunnel.</summary>
public abstract record TransportTarget(string Id);

/// <summary>Local serial port: COM5, /dev/ttyUSB0, /dev/cu.usbserial-…</summary>
public sealed record SerialTarget(string Id, string PortName, SerialSettings Settings) : TransportTarget(Id);

/// <summary>The instrument is a TCP server; the host connects.</summary>
public sealed record TcpClientTarget(string Id, TcpEndpoint Endpoint) : TransportTarget(Id);

/// <summary>The instrument is a TCP client; the host listens and takes the first connection.</summary>
public sealed record TcpListenTarget(string Id, IPEndPoint Local) : TransportTarget(Id)
{
    public TlsOptions? Tls { get; init; }
}

/// <summary>Opens the final transport the host hands to an ASTM session.</summary>
public interface ITransportProvider
{
    bool Supports(TransportTarget target);

    ValueTask<Stream> OpenAsync(TransportTarget target, CancellationToken cancellationToken);
}

/// <summary>Chooses the first provider that supports the target: local providers and tunnels (PortGate) side by side.</summary>
public sealed class TransportRouter(IEnumerable<ITransportProvider> providers) : ITransportProvider
{
    private readonly ITransportProvider[] _providers = providers.ToArray();

    public bool Supports(TransportTarget target) => _providers.Any(p => p.Supports(target));

    public ValueTask<Stream> OpenAsync(TransportTarget target, CancellationToken cancellationToken) =>
        (_providers.FirstOrDefault(p => p.Supports(target)) ?? throw new NotSupportedException($"No transport provider for {target.GetType().Name} '{target.Id}'."))
            .OpenAsync(target, cancellationToken);
}

public static class TransportKnowledgeExtensions
{
    /// <summary>Serial settings declared by the plugin, or null when its profile has no serial interface.</summary>
    public static SerialSettings? DefaultSerialSettings(this TransportKnowledge? knowledge) => knowledge?.Serial?.Default;

    /// <summary>Checks a chosen setting against what the instrument supports according to its manual.</summary>
    public static IReadOnlyList<string> Validate(this TransportKnowledge? knowledge, SerialSettings settings)
    {
        var problems = new List<string>();
        if (knowledge?.Serial is { } serial)
        {
            if (serial.SupportedBaudRates.Count > 0 && !serial.SupportedBaudRates.Contains(settings.BaudRate))
                problems.Add($"{settings.BaudRate} baud is not among the documented rates ({string.Join(", ", serial.SupportedBaudRates)})");
            if (serial.RequiresDtr && !settings.Dtr)
                problems.Add("the instrument requires DTR asserted");
        }
        return problems;
    }
}

/// <summary>Local COM and TCP/TLS (client and server).</summary>
public sealed class LocalTransportProvider : ITransportProvider
{
    public bool Supports(TransportTarget target) => target is SerialTarget or TcpClientTarget or TcpListenTarget;

    public async ValueTask<Stream> OpenAsync(TransportTarget target, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case SerialTarget serial:
                return LocalSerial.Open(serial.PortName, serial.Settings);
            case TcpClientTarget tcp:
                return await ConnectAsync(tcp.Endpoint, cancellationToken).ConfigureAwait(false);
            case TcpListenTarget listen:
                using (var listener = new TcpListener(listen.Local))
                {
                    listener.Start(1);
                    var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    return await SecureAsync(client, listen.Tls, asServer: true, listen.Local.Address.ToString(), cancellationToken).ConfigureAwait(false);
                }
            default:
                throw new NotSupportedException(target.GetType().Name);
        }
    }

    /// <summary>Accepts one connection on <paramref name="listener"/> with optional TLS (server side); the socket is disposed with the stream.</summary>
    public static async Task<Stream> AcceptAsync(TcpListener listener, TlsOptions? tls, CancellationToken cancellationToken)
    {
        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SecureAsync(client, tls, asServer: true, ((IPEndPoint)listener.LocalEndpoint).Address.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>TCP client with optional TLS; the socket is disposed with the returned stream.</summary>
    public static async Task<Stream> ConnectAsync(TcpEndpoint endpoint, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(endpoint.ConnectTimeout);
                await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
            }
            return await SecureAsync(client, endpoint.Tls, asServer: false, endpoint.Host, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<Stream> SecureAsync(TcpClient client, TlsOptions? tls, bool asServer, string host, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        if (tls is null)
            return new OwnedStream(client.GetStream(), client);
        var secure = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, tls.CertificateValidation);
        try
        {
            if (asServer)
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = tls.ServerCertificate ?? throw new InvalidOperationException("A TLS listener needs a server certificate."),
                    ClientCertificateRequired = tls.RequireClientCertificate,
                    EnabledSslProtocols = tls.Protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, cancellationToken).ConfigureAwait(false);
            else
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = tls.TargetHost ?? host,
                    ClientCertificates = tls.ClientCertificates,
                    EnabledSslProtocols = tls.Protocols,
                }, cancellationToken).ConfigureAwait(false);
            return new OwnedStream(secure, client);
        }
        catch
        {
            await secure.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }
    }
}

/// <summary>Stream that disposes its owner (socket, serial port) with it.</summary>
public sealed class OwnedStream(Stream inner, IDisposable owner, bool cancelPendingReads = false) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        // Some platform streams (serial) do not observe cancellation of a pending read: stop waiting for it instead.
        cancelPendingReads && cancellationToken.CanBeCanceled
            ? new ValueTask<int>(inner.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(cancellationToken))
            : inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private int _disposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            owner.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
